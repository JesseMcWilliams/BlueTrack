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
    IdentityProviderRepository identityProviderRepository)
{
    private static readonly UserRights None = new([], []);

    public async Task<UserRights> ResolveAsync(ClaimsPrincipal principal)
    {
        // Only WindowsIntegrated is wired (AuthenticationExtensions.cs) -- once
        // OIDC/SAML are added, the provider needs to come from the scheme that
        // actually authenticated this request, not be hardcoded here.
        var provider = await identityProviderRepository.GetByTypeAsync("WindowsIntegrated");
        if (provider is null)
        {
            return None;
        }

        var groupIdentifiers = GroupIdentifierExtractor.GetGroupIdentifiers(principal);
        if (groupIdentifiers.Count == 0)
        {
            return None;
        }

        var roleNames = await authorizationRepository.GetMatchedRoleNamesAsync(provider.ProviderKey, groupIdentifiers);
        var permissionNames = await authorizationRepository.GetEffectivePermissionNamesAsync(provider.ProviderKey, groupIdentifiers);

        return new UserRights(roleNames, permissionNames);
    }
}
