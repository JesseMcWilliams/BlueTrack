using System.Security.Claims;
using System.Security.Principal;

namespace BlueTrack.Api.Auth;

/// <summary>
/// web.app_user.ExternalIdentifier is documented as "Windows SID, OIDC
/// sub/object ID, or SAML NameID" -- for WindowsIntegrated and DevFakeAuth
/// (both real Negotiate underneath) that's the SID off the Windows access
/// token, not the display name (DOMAIN\user). Shared by CurrentUserResolver
/// and UserRightsResolver -- both need the same identity key.
///
/// OIDC/SAML (D-84): prefers the standard ClaimTypes.NameIdentifier claim
/// over Identity.Name -- ASP.NET Core's OIDC handler maps the token's "sub"
/// claim to ClaimTypes.NameIdentifier by default (not to Identity.Name,
/// which depends on ClaimsIdentity.NameClaimType and isn't populated unless
/// a claim literally typed "name" exists), and ITfoxtec's SAML
/// ClaimsIdentity follows the same NameIdentifier convention for the
/// assertion's NameID. Falls back to Identity.Name only if
/// NameIdentifier is absent, so nothing changes for WindowsIdentity (which
/// never reaches this branch) or any future provider that only populates
/// Name.
/// </summary>
public static class ExternalIdentifierReader
{
    public static string? Resolve(ClaimsPrincipal principal) =>
        principal.Identity is WindowsIdentity { User: not null } windowsIdentity
            ? windowsIdentity.User.Value
            : principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.Identity?.Name;
}
