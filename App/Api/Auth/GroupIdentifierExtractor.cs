using System.Security.Claims;
using System.Security.Principal;

namespace BlueTrack.Api.Auth;

/// <summary>
/// Claims Normalization Pipeline step 2 (Design_Authorization_Model.md):
/// extracts raw group identifiers regardless of source shape. The
/// WindowsIntegrated shape reads SIDs straight off the Windows access token
/// (no extra AD/LDAP round trip), matching that design doc's own "Windows
/// token SIDs" wording and web.app_user's ExternalIdentifier contract
/// ("Windows SID, OIDC sub/object ID, or SAML NameID"). OIDC's groups claim
/// and SAML's group attribute aren't implemented here yet, matching
/// AuthenticationExtensions.cs's own note that those providers aren't
/// registered yet either.
/// </summary>
public static class GroupIdentifierExtractor
{
    public static IReadOnlyList<string> GetGroupIdentifiers(ClaimsPrincipal principal)
    {
        if (principal.Identity is WindowsIdentity { Groups: not null } windowsIdentity)
        {
            return windowsIdentity.Groups.Select(g => g.Value).ToList();
        }

        return [];
    }

    /// <summary>
    /// DevFakeAuth's substitution (Design_Authentication_Architecture.md):
    /// "resolves against a small dev-only mapping (local Windows username
    /// -> simulated app role or group)" -- the identifier is the
    /// authenticated Windows username itself, not a group SID, matched
    /// against identity_group_role_map rows scoped to the DevFakeAuth
    /// provider (see 18_BlueTrack_DevFakeAuthSeed.sql).
    /// </summary>
    public static IReadOnlyList<string> GetDevFakeAuthIdentifiers(ClaimsPrincipal principal)
    {
        var name = principal.Identity?.Name;
        return string.IsNullOrEmpty(name) ? [] : [name];
    }
}
