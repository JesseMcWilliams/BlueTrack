using System.Security.Claims;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Auth;

public sealed record UserRights(IReadOnlyList<string> RoleNames, IReadOnlyList<string> PermissionNames);

/// <summary>
/// Claims Normalization Pipeline steps 2-4 (Design_Authorization_Model.md),
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
    AuditLogger auditLogger)
{
    private static readonly UserRights None = new([], []);

    /// <summary>Cache-first. Populates the cache and logs a Logon event (D-11) on a miss. Shared by PermissionClaimsTransformation and /api/me.</summary>
    public async Task<UserRights> ResolveAsync(ClaimsPrincipal principal)
    {
        var provider = await negotiateProviderResolver.ResolveAsync();
        if (provider is null)
        {
            return None;
        }

        var externalIdentifier = ExternalIdentifierReader.Resolve(principal);
        if (string.IsNullOrEmpty(externalIdentifier))
        {
            return None;
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
        }

        return rights;
    }

    /// <summary>Bypasses the cache, always resolves live, and refreshes the cache -- self-service Reload Rights (D-14). Not a logon: no audit event here.</summary>
    public async Task<UserRights> RefreshAsync(ClaimsPrincipal principal)
    {
        var provider = await negotiateProviderResolver.ResolveAsync();
        if (provider is null)
        {
            return None;
        }

        var externalIdentifier = ExternalIdentifierReader.Resolve(principal);
        if (string.IsNullOrEmpty(externalIdentifier))
        {
            return None;
        }

        var rights = await ResolveLiveAsync(principal, provider);
        await rightsCache.SetAsync(provider.ProviderKey, externalIdentifier, rights);
        return rights;
    }

    private async Task<UserRights> ResolveLiveAsync(ClaimsPrincipal principal, IdentityProviderConfig provider)
    {
        var groupIdentifiers = provider.ProviderType == "DevFakeAuth"
            ? GroupIdentifierExtractor.GetDevFakeAuthIdentifiers(principal)
            : GroupIdentifierExtractor.GetGroupIdentifiers(principal);

        if (groupIdentifiers.Count == 0)
        {
            return None;
        }

        var roleNames = await authorizationRepository.GetMatchedRoleNamesAsync(provider.ProviderKey, groupIdentifiers);
        var permissionNames = await authorizationRepository.GetEffectivePermissionNamesAsync(provider.ProviderKey, groupIdentifiers);

        return new UserRights(roleNames, permissionNames);
    }
}
