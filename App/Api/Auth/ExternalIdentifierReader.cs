using System.Security.Claims;
using System.Security.Principal;

namespace BlueTrack.Api.Auth;

/// <summary>
/// web.app_user.ExternalIdentifier is documented as "Windows SID, OIDC
/// sub/object ID, or SAML NameID" -- for WindowsIntegrated and DevFakeAuth
/// (both real Negotiate underneath) that's the SID off the Windows access
/// token, not the display name (DOMAIN\user). Shared by CurrentUserResolver
/// and UserRightsResolver -- both need the same identity key.
/// </summary>
public static class ExternalIdentifierReader
{
    public static string? Resolve(ClaimsPrincipal principal) =>
        principal.Identity is WindowsIdentity { User: not null } windowsIdentity
            ? windowsIdentity.User.Value
            : principal.Identity?.Name;
}
