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

    /// <summary>Exactly one of: an API key, or a token an identity provider issued (D-243 phase 2).</summary>
    public sealed record TokenRequest(string? ApiKey, string? IdToken = null);

    [HttpPost]
    public async Task<IActionResult> Exchange([FromBody] TokenRequest request, [FromServices] IdpTokenValidator idpTokens, [FromServices] ApiClientMappingRepository mappings)
    {
        var hasKey = !string.IsNullOrWhiteSpace(request.ApiKey);
        var hasIdToken = !string.IsNullOrWhiteSpace(request.IdToken);
        if (hasKey == hasIdToken)
        {
            return BadRequest(new { title = "Send one credential", status = 400, detail = "Send either apiKey or idToken, not both and not neither." });
        }

        var from = HttpContext.Connection.RemoteIpAddress?.ToString();
        var failureKey = $"api-token-failures:{from}";
        if (cache.TryGetValue(failureKey, out int failures) && failures >= MaxFailuresPerMinute)
        {
            Response.Headers.RetryAfter = "60";
            return StatusCode(StatusCodes.Status429TooManyRequests, new { title = "Too many failed attempts", status = 429, detail = "Wait a minute and try again." });
        }

        return hasKey
            ? await ExchangeKeyAsync(request.ApiKey!, from, failureKey, failures)
            : await ExchangeIdpTokenAsync(request.IdToken!, from, failureKey, failures, idpTokens, mappings);
    }

    private async Task<IActionResult> RefuseAsync(string reason, string shown, string? from, string failureKey, int failures, string? entityKey)
    {
        cache.Set(failureKey, failures + 1, TimeSpan.FromMinutes(1));
        if (await keys.GetSystemUserKeyAsync() is { } systemUser)
        {
            await auditLogger.LogAsync("ApiTokenRejected", systemUser, "api_key", entityKey,
                detail: $"Token request refused ({reason}); {shown}; from {from}");
        }
        return Unauthorized(new { title = "Unauthorized", status = 401, detail = "The credential is not valid." });
    }

    private async Task<IActionResult> ExchangeKeyAsync(string apiKey, string? from, string failureKey, int failures)
    {
        var prefix = ApiKeyGenerator.PrefixOf(apiKey);
        var credential = prefix is null ? null : await keys.FindByPrefixAsync(prefix);
        string? refusal = null;
        if (credential is null || !ApiKeyGenerator.Matches(apiKey, credential.KeyHash))
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
            // Only the prefix of what was sent, and only when it looks like a key (a prefix is 11 URL-safe characters).
            var shown = prefix is not null && prefix.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? prefix : "(not a key)";
            return await RefuseAsync(refusal, $"key prefix {shown}", from, failureKey, failures, credential?.ApiKeyKey.ToString());
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

    /// <summary>D-243 (phase 2): a token from an enabled OIDC identity provider, exchanged for the role its API client mapping gives.</summary>
    private async Task<IActionResult> ExchangeIdpTokenAsync(
        string idToken, string? from, string failureKey, int failures, IdpTokenValidator idpTokens, ApiClientMappingRepository mappings)
    {
        var caller = await idpTokens.ValidateAsync(idToken);
        if (caller is null)
        {
            return await RefuseAsync("identity provider token not valid", "no identity provider trusts it, or it is expired or altered", from, failureKey, failures, null);
        }

        var match = await mappings.FindMatchAsync(caller.ProviderKey, caller.ClientId, caller.Groups);
        if (match is null || match.RoleName == ApiAccessRules.AdminRoleName)
        {
            return await RefuseAsync("no API client mapping", $"provider '{caller.ProviderName}', client {caller.ClientId ?? "(none)"}, {caller.Groups.Count} group(s)", from, failureKey, failures, null);
        }

        var provider = await providers.GetByTypeAsync(ApiAccessRules.ProviderType);
        if (provider is null) return StatusCode(StatusCodes.Status503ServiceUnavailable, new { title = "API access is not set up", status = 503 });

        // The caller's own identity (not the mapping's): the same client or person is the same app_user each time.
        var subject = $"apiclient-{caller.ProviderKey}-{(caller.Subject.Length <= 200 ? caller.Subject : caller.Subject[..200])}";
        var user = await appUsers.UpsertOnLoginAsync(provider.ProviderKey, subject, $"API client: {caller.DisplayName}", null);
        await mappings.TouchLastUsedAsync(match.ApiClientMappingKey);
        var (token, expiresIn) = await tokens.IssueForClientAsync(subject, caller.DisplayName, match.ApiClientMappingKey, match.AppRoleKey);
        await auditLogger.LogAsync("ApiTokenIssued", user.UserKey, "api_client_mapping", match.ApiClientMappingKey.ToString(),
            detail: $"Token issued to '{caller.DisplayName}' from identity provider '{caller.ProviderName}' (matched {match.MatchType} {match.MatchValue}, role {match.RoleName}); from {from}");
        return Ok(new { access_token = token, token_type = "Bearer", expires_in = expiresIn });
    }
}
