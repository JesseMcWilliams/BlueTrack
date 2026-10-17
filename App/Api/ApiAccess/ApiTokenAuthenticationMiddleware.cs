using System.Security.Claims;
using BlueTrack.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Caching.Memory;

namespace BlueTrack.Api.ApiAccess;

/// <summary>
/// D-243: authenticates a request that carries "Authorization: Bearer
/// &lt;token&gt;" with a token from POST /api/auth/token. It is middleware,
/// not an authentication scheme, on purpose: AuthenticationExtensions.cs
/// must not gain a policy scheme or change the default scheme (that broke
/// Windows sign-in before, D-86 and the class comment there). It runs after
/// the cookie-preference middleware in Program.cs and before authorization.
///
/// A request with a bearer header is never left to fall back to Windows or
/// the cookie: an invalid, expired, disabled or deleted key is a 401. The
/// key's state is checked on every request, from a one-minute cache, so
/// disabling a key takes effect within about a minute even though its token
/// has up to ten minutes left. What the caller may do comes from the key's
/// role (UserRightsResolver), not from the token.
/// </summary>
public sealed class ApiTokenAuthenticationMiddleware(RequestDelegate next)
{
    public const string BearerPrefix = "Bearer ";
    public static readonly TimeSpan StateCacheTime = TimeSpan.FromMinutes(1);
    public const string RateLimitCacheKey = "api-rate-limit";
    public const string MappingStateCachePrefix = "apiclient-state:";

    private sealed class RateCounter
    {
        public int Count;
    }

    public static bool HasBearerToken(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase);

    public async Task InvokeAsync(
        HttpContext context, ApiTokenService tokens, ApiKeyRepository keys, IClaimsTransformation claimsTransformation,
        IMemoryCache cache, TimeProvider timeProvider, BlueTrack.Api.Data.AppConfigRepository appConfig, ApiClientMappingRepository mappings)
    {
        if (!HasBearerToken(context.Request))
        {
            await next(context);
            return;
        }

        var token = context.Request.Headers.Authorization.ToString()[BearerPrefix.Length..].Trim();
        if (token.StartsWith(ApiKeyGenerator.KeyStart, StringComparison.Ordinal))
        {
            await RejectAsync(context, "An API key is not a token: exchange it at POST /api/auth/token and send the token it returns.");
            return;
        }

        var identity = await tokens.ValidateAsync(token);
        if (identity is null)
        {
            await RejectAsync(context, "The token is invalid or has expired.");
            return;
        }

        string displayName;
        int? keyLimit = null;
        if (identity.ApiKeyKey is { } apiKeyKey)
        {
            var state = await cache.GetOrCreateAsync($"apikey-state:{apiKeyKey}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = StateCacheTime;
                return await keys.GetStateAsync(apiKeyKey);
            });
            if (state is not { IsEnabled: true } || state.ExpiresAt <= timeProvider.GetUtcNow().UtcDateTime)
            {
                await RejectAsync(context, "The API key is disabled, expired or deleted.");
                return;
            }
            displayName = state.DisplayName;
            keyLimit = state.RateLimitPerMinute;
        }
        else
        {
            // A token from an identity provider: its mapping must still be enabled and still give the same role.
            var mappingKey = identity.MappingKey!.Value;
            var state = await cache.GetOrCreateAsync($"{MappingStateCachePrefix}{mappingKey}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = StateCacheTime;
                return await mappings.GetStateAsync(mappingKey);
            });
            if (state is not { IsEnabled: true } || state.AppRoleKey != identity.RoleKey)
            {
                await RejectAsync(context, "The API client mapping is disabled, changed or deleted.");
                return;
            }
            displayName = identity.DisplayName;
        }

        // A fixed one-minute window per key. The limit is a setting (read at most once a minute).
        var limit = keyLimit ?? await cache.GetOrCreateAsync(RateLimitCacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = StateCacheTime;
            return await appConfig.GetApiRateLimitPerMinuteAsync();
        });
        var now = timeProvider.GetUtcNow();
        var window = now.ToUnixTimeSeconds() / 60;
        var counter = cache.GetOrCreate($"apikey-rate:{identity.Subject}:{window}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(2);
            return new RateCounter();
        })!;
        if (Interlocked.Increment(ref counter.Count) > limit)
        {
            var retryAfter = (int)(((window + 1) * 60) - now.ToUnixTimeSeconds());
            context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
            context.Response.Headers.RetryAfter = Math.Max(1, retryAfter).ToString();
            await context.Response.WriteAsJsonAsync(new { title = "Too many requests", status = 429, detail = $"This API key may make {limit} requests a minute." });
            return;
        }

        var claims = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, identity.Subject),
                new Claim("name", identity.ApiKeyKey is null ? $"API client: {displayName}" : $"API key: {displayName}"),
            ],
            authenticationType: "BlueTrack.ApiToken");
        if (identity.RoleKey is { } roleKey) claims.AddClaim(new Claim(BlueTrackClaimTypes.ApiRoleKey, roleKey.ToString()));
        var principal = new ClaimsPrincipal(claims);
        ExternalIdentityClaims.MarkProvider(principal, ApiAccessRules.ProviderType);

        context.User = await claimsTransformation.TransformAsync(principal);
        await next(context);
    }

    private static async Task RejectAsync(HttpContext context, string detail)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers.WWWAuthenticate = "Bearer error=\"invalid_token\"";
        await context.Response.WriteAsJsonAsync(new { title = "Unauthorized", status = StatusCodes.Status401Unauthorized, detail });
    }
}
