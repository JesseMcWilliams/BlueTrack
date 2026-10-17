using BlueTrack.Api.ApiAccess;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-243: exchanges an API key for a short-lived token. Anonymous by nature
/// (the key is the credential), so it is the one place the key is ever sent;
/// every other call uses the token. Every exchange is audited, a refusal
/// without saying why (unknown, wrong, disabled and expired keys all look
/// the same to the caller), and repeated refusals from one address are
/// throttled.
/// </summary>
[ApiController]
[Route("api/auth/token")]
[AllowAnonymous]
public sealed class ApiTokenController(
    ApiKeyRepository keys,
    ApiTokenService tokens,
    AppUserRepository appUsers,
    IdentityProviderRepository providers,
    AuditLogger auditLogger,
    IMemoryCache cache,
    TimeProvider timeProvider) : ControllerBase
{
    public const int MaxFailuresPerMinute = 10;

    public sealed record TokenRequest(string? ApiKey);

    [HttpPost]
    public async Task<IActionResult> Exchange([FromBody] TokenRequest request)
    {
        var from = HttpContext.Connection.RemoteIpAddress?.ToString();
        var failureKey = $"api-token-failures:{from}";
        if (cache.TryGetValue(failureKey, out int failures) && failures >= MaxFailuresPerMinute)
        {
            Response.Headers.RetryAfter = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests, new { title = "Too many failed attempts", status = 429, detail = "Wait a minute and try again." });
        }

        var prefix = ApiKeyGenerator.PrefixOf(request.ApiKey);
        var credential = prefix is null ? null : await keys.FindByPrefixAsync(prefix);
        string? refusal = null;
        if (credential is null || !ApiKeyGenerator.Matches(request.ApiKey!, credential.KeyHash))
        {
            refusal = "unknown or wrong key";
        }
        else if (!credential.IsEnabled)
        {
            refusal = "key disabled";
        }
        else if (credential.ExpiresAt <= timeProvider.GetUtcNow().UtcDateTime)
        {
            refusal = "key expired";
        }

        if (refusal is not null)
        {
            cache.Set(failureKey, failures + 1, TimeSpan.FromMinutes(1));
            if (await keys.GetSystemUserKeyAsync() is { } systemUser)
            {
                // Only the prefix of what was sent, and only when it looks like a key (a prefix is 11 URL-safe characters).
                var shown = prefix is not null && prefix.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? prefix : "(not a key)";
                await auditLogger.LogAsync("ApiTokenRejected", systemUser, "api_key", credential?.ApiKeyKey.ToString(),
                    detail: $"Token request refused ({refusal}); key prefix {shown}; from {from}");
            }
            return Unauthorized(new { title = "Unauthorized", status = 401, detail = "The API key is not valid." });
        }

        var provider = await providers.GetByTypeAsync(ApiAccessRules.ProviderType);
        if (provider is null) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { title = "API access is not set up", status = 503 });

        var user = await appUsers.UpsertOnLoginAsync(provider.ProviderKey, ApiAccessRules.ExternalIdentifierFor(credential!.ApiKeyKey), $"API key: {credential.DisplayName}", null);
        await keys.TouchLastUsedAsync(credential.ApiKeyKey, from);
        var (token, expiresIn) = await tokens.IssueAsync(credential.ApiKeyKey, credential.DisplayName);
        await auditLogger.LogAsync("ApiTokenIssued", user.UserKey, "api_key", credential.ApiKeyKey.ToString(),
            detail: $"Token issued to API key '{credential.DisplayName}' ({prefix}); from {from}");
        return Ok(new { access_token = token, token_type = "Bearer", expires_in = expiresIn });
    }
}
