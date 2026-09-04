using System.Security.Claims;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Auth;

public sealed record UserRights(IReadOnlyList<string> RoleNames, IReadOnlyList<string> PermissionNames);

/// <summary>
/// Claims Normalization Pipeline steps 2-4 (Design_Authorization_Model.md):
/// extract this principal's raw group identifiers, resolve them against
/// identity_group_role_map for the active provider, and union the matched
/// roles' permissions. Shared by PermissionClaimsTransformation (adds
/// permission claims to every authenticated request) and MeController's
/// self-service Reload Rights endpoint (D-14) -- both need the exact same
/// live resolution, just triggered differently.
/// </summary>
public sealed class UserRightsResolver(
    AuthorizationRepository authorizationRepository,
    NegotiateProviderResolver negotiateProviderResolver)
{
    private static readonly UserRights None = new([], []);

    public async Task<UserRights> ResolveAsync(ClaimsPrincipal principal)
    {
        // WindowsIntegrated vs. DevFakeAuth (both Negotiate) is resolved by
        // NegotiateProviderResolver; OIDC/SAML still aren't wired
        // (AuthenticationExtensions.cs), so there's no third case here yet.
        var provider = await negotiateProviderResolver.ResolveAsync();
        if (provider is null)
        {
            return None;
        }

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
