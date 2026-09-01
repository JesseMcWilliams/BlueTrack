using System.Security.Claims;
using System.Security.Principal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// Demonstrates the Login Flow (Design_Authentication_Architecture.md):
/// step 6 resolves the normalized identity against web.app_user (upserting
/// on every login), step 7 resolves the user's roles/permissions via
/// UserRightsResolver. Only Windows Integrated is wired currently (see
/// AuthenticationExtensions) -- this controller doesn't yet do the full
/// claims-normalization pipeline's step 5 for OIDC/SAML, since those
/// providers aren't wired yet either.
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize]
public sealed class MeController(
    IdentityProviderRepository providerRepository,
    AppUserRepository appUserRepository,
    UserRightsResolver rightsResolver) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetCurrentUser()
    {
        var externalIdentifier = ResolveExternalIdentifier(User);
        if (string.IsNullOrEmpty(externalIdentifier))
        {
            return Unauthorized();
        }

        var provider = await providerRepository.GetByTypeAsync("WindowsIntegrated");
        if (provider is null)
        {
            // identity_provider_config has no WindowsIntegrated row yet -- this
            // is expected until the Identity Providers admin screen (or a seed
            // script) actually registers one. Not an app bug.
            return Problem(
                title: "No WindowsIntegrated provider configured",
                detail: "Register a WindowsIntegrated row in web.identity_provider_config before this endpoint can resolve app_user.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var user = await appUserRepository.UpsertOnLoginAsync(
            provider.ProviderKey,
            externalIdentifier,
            displayName: User.Identity?.Name,
            email: null);

        var rights = await rightsResolver.ResolveAsync(User);

        return Ok(new
        {
            user.UserKey,
            user.ExternalIdentifier,
            user.DisplayName,
            user.FirstLogin,
            user.LastLogin,
            rights.RoleNames,
            rights.PermissionNames
        });
    }

    /// <summary>
    /// Self-service "Reload My Rights" (D-14). See
    /// PermissionClaimsTransformation's own comment on why this doesn't yet
    /// mean anything different from a normal request -- there's no session
    /// cache to actually invalidate here yet, so every request is already a
    /// live re-resolution. This endpoint exists so the frontend has a real
    /// action to call and a fresh result to show, matching the design's
    /// intended UX ahead of that session-cache follow-up.
    /// </summary>
    [HttpPost("reload-rights")]
    public async Task<IActionResult> ReloadRights()
    {
        var rights = await rightsResolver.ResolveAsync(User);
        return Ok(rights);
    }

    // web.app_user.ExternalIdentifier is documented as "Windows SID, OIDC
    // sub/object ID, or SAML NameID" -- for WindowsIntegrated that's the
    // SID off the Windows access token, not the display name (DOMAIN\user).
    private static string? ResolveExternalIdentifier(ClaimsPrincipal user) =>
        user.Identity is WindowsIdentity { User: not null } windowsIdentity
            ? windowsIdentity.User.Value
            : user.Identity?.Name;
}
