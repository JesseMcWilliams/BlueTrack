using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// Demonstrates the Login Flow (Design_Authentication_Architecture.md):
/// step 6 resolves the normalized identity against web.app_user (upserting
/// on every login, via CurrentUserResolver), step 7 resolves the user's
/// roles/permissions via UserRightsResolver. Only Windows Integrated is
/// wired currently (see AuthenticationExtensions) -- this controller
/// doesn't yet do the full claims-normalization pipeline's step 5 for
/// OIDC/SAML, since those providers aren't wired yet either.
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize]
public sealed class MeController(
    CurrentUserResolver currentUserResolver,
    UserRightsResolver rightsResolver) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetCurrentUser()
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null)
        {
            // identity_provider_config has no WindowsIntegrated row yet -- this
            // is expected until the Identity Providers admin screen (or a seed
            // script) actually registers one. Not an app bug.
            return Problem(
                title: "No WindowsIntegrated provider configured",
                detail: "Register a WindowsIntegrated row in web.identity_provider_config before this endpoint can resolve app_user.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

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
}
