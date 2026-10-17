using System.Security.Claims;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Auth;

public sealed record UserRights(IReadOnlyList<string> RoleNames, IReadOnlyList<string> PermissionNames);

/// <summary>
/// Claims Normalization Pipeline steps 2-4 (Design_Authorization-Model.md),
/// now cached per identity (D-13/D-82) -- a live group -> role ->
/// permission resolution only happens on a cache miss (ResolveAsync) or an
/// explicit refresh (RefreshAsync: D-14's self-service Reload Rights).
/// A cache miss is also BlueTrack's logon-detection signal (D-11): there's
/// no ASP.NET Core cookie/session concept in this app, so "a live
/// resolution was actually needed" stands in for "a new session started."
/// </summary>
public sealed class UserRightsResolver(
    AuthorizationRepository authorizationRepository,
    NegotiateProviderResolver negotiateProviderResolver,
    UserRightsCache rightsCache,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger,
    BreakGlassAccountRepository breakGlassAccountRepository,
    ApiAccess.ApiKeyRepository apiKeyRepository,
    ApiAccess.ApiClientMappingRepository apiClientMappingRepository,
    Notifications.BreakGlassAlerter breakGlassAlerter,
    IHttpContextAccessor httpContextAccessor)
{
    private static readonly UserRights None = new([], []);

    /// <summary>Cache-first. Populates the cache and logs a Logon event (D-11) on a miss. Shared by PermissionClaimsTransformation and /api/me.</summary>
    public async Task<UserRights> ResolveAsync(ClaimsPrincipal principal)
    {
        // D-197: a disabled provider grants nothing -- including to OIDC/SAML
        // sessions signed in before an admin disabled it.
        var provider = await negotiateProviderResolver.ResolveAsync(principal);
        if (provider is null || !provider.IsEnabled)
        {
            return None;
        }

        var externalIdentifier = ExternalIdentifierReader.Resolve(principal);
        if (string.IsNullOrEmpty(externalIdentifier))
        {
            return None;
        }

        // D-243: an API caller's rights are looked up on every request (two small queries): never cached, so a
        // changed role takes effect at once, and not a "logon" (the token exchange was audited).
        if (provider.ProviderType == ApiAccess.ApiAccessRules.ProviderType)
        {
            return await ResolveApiAccessAsync(principal, externalIdentifier);
        }

        var cached = await rightsCache.GetAsync(provider.ProviderKey, externalIdentifier);
        if (cached is not null)
        {
            return cached;
        }

        var rights = await ResolveLiveAsync(principal, provider);
        await rightsCache.SetAsync(provider.ProviderKey, externalIdentifier, rights);

        var user = await currentUserResolver.ResolveAsync(principal);
        if (user is not null)
        {
            await auditLogger.LogAsync("Logon", user.UserKey, detail: $"Session established via {provider.ProviderType}");

            // D-216: a designated break-glass account's sign-in is audited on its own and alerts by email.
            if (await breakGlassAccountRepository.IsBreakGlassAsync(externalIdentifier))
            {
                var accountName = user.DisplayName ?? externalIdentifier;
                await auditLogger.LogAsync("BreakGlassLogon", user.UserKey, "app_user", user.UserKey.ToString(),
                    detail: $"Break-glass account {accountName} signed in via {provider.ProviderType}");
                _ = breakGlassAlerter.SendInBackground(accountName, provider.ProviderType,
                    httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString(), DateTime.UtcNow);
            }
        }

        return rights;
    }

    /// <summary>Bypasses the cache, always resolves live, and refreshes the cache -- self-service Reload Rights (D-14). Not a logon: no audit event here.</summary>
    public async Task<UserRights> RefreshAsync(ClaimsPrincipal principal)
    {
        // D-197: a disabled provider grants nothing -- including to OIDC/SAML
        // sessions signed in before an admin disabled it.
        var provider = await negotiateProviderResolver.ResolveAsync(principal);
        if (provider is null || !provider.IsEnabled)
        {
            return None;
        }

        var externalIdentifier = ExternalIdentifierReader.Resolve(principal);
        if (string.IsNullOrEmpty(externalIdentifier))
        {
            return None;
        }

        if (provider.ProviderType == ApiAccess.ApiAccessRules.ProviderType)
        {
            return await ResolveApiAccessAsync(principal, externalIdentifier);
        }

        var rights = await ResolveLiveAsync(principal, provider);
        await rightsCache.SetAsync(provider.ProviderKey, externalIdentifier, rights);
        return rights;
    }

    /// <summary>
    /// D-243: an API key has its one role; a caller with an identity provider's token has the role its mapping
    /// gave it (carried in the checked token, never from the provider's claims). Either way minus what an API
    /// caller can never have, and never Admin.
    /// </summary>
    private async Task<UserRights> ResolveApiAccessAsync(ClaimsPrincipal principal, string externalIdentifier)
    {
        const string keyPrefix = "apikey-";
        if (externalIdentifier.StartsWith(keyPrefix, StringComparison.Ordinal) && int.TryParse(externalIdentifier[keyPrefix.Length..], out var apiKeyKey))
        {
            var keyRights = await apiKeyRepository.GetRightsAsync(apiKeyKey);
            return keyRights is null || keyRights.RoleName == ApiAccess.ApiAccessRules.AdminRoleName
                ? None
                : new UserRights([keyRights.RoleName], keyRights.PermissionNames);
        }

        if (externalIdentifier.StartsWith("apiclient-", StringComparison.Ordinal)
            && int.TryParse(principal.FindFirst(BlueTrackClaimTypes.ApiRoleKey)?.Value, out var roleKey))
        {
            var clientRights = await apiClientMappingRepository.GetRoleRightsAsync(roleKey);
            return clientRights is null ? None : new UserRights([clientRights.RoleName], clientRights.PermissionNames);
        }

        return None;
    }

    private async Task<UserRights> ResolveLiveAsync(ClaimsPrincipal principal, IdentityProviderConfig provider)
    {
        var groupIdentifiers = provider.ProviderType switch
        {
            "DevFakeAuth" => GroupIdentifierExtractor.GetDevFakeAuthIdentifiers(principal),
            "OIDC" => GroupIdentifierExtractor.GetClaimBasedGroupIdentifiers(principal, ProviderSettingsReader.ReadGroupClaimType(provider)),
            "SAML" => GroupIdentifierExtractor.GetClaimBasedGroupIdentifiers(principal, ProviderSettingsReader.ReadGroupClaimType(provider)),
            _ => GroupIdentifierExtractor.GetGroupIdentifiers(principal)
        };

        if (groupIdentifiers.Count == 0)
        {
            return None;
        }

        var roleNames = await authorizationRepository.GetMatchedRoleNamesAsync(provider.ProviderKey, groupIdentifiers);
        var permissionNames = await authorizationRepository.GetEffectivePermissionNamesAsync(provider.ProviderKey, groupIdentifiers);

        return new UserRights(roleNames, permissionNames);
    }
}
