using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

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
    UserRightsResolver rightsResolver,
    UserPreferenceRepository userPreferenceRepository,
    AuditLogger auditLogger) : ControllerBase
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
        var preferences = await userPreferenceRepository.GetAllForUserAsync(user.UserKey);

        return Ok(new
        {
            user.UserKey,
            user.ExternalIdentifier,
            user.DisplayName,
            user.FirstLogin,
            user.LastLogin,
            rights.RoleNames,
            rights.PermissionNames,
            Preferences = preferences
        });
    }

    /// <summary>
    /// Generalized self-service preference storage (D-93) -- Theme is the
    /// first consumer, but this endpoint isn't Theme-specific; any future
    /// per-user setting reuses the same shape.
    /// </summary>
    [HttpPut("preferences/{key}")]
    public async Task<IActionResult> SetPreference(string key, [FromBody] SetUserPreferenceRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await userPreferenceRepository.SetAsync(user.UserKey, key, request.Value);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "user_preference", key,
            detail: $"Preference '{key}' set to '{request.Value}'");

        return NoContent();
    }

    /// <summary>
    /// Self-service "Reload My Rights" (D-14). Now genuinely meaningful
    /// (D-82): permissions are cached per identity (UserRightsCache), so
    /// this bypasses that cache and re-resolves live from current group
    /// membership, then refreshes the cache -- exactly the behavior D-13
    /// describes ("re-fetches current group membership... in real time").
    /// </summary>
    [HttpPost("reload-rights")]
    public async Task<IActionResult> ReloadRights()
    {
        var rights = await rightsResolver.RefreshAsync(User);

        var user = await currentUserResolver.ResolveAsync(User);
        if (user is not null)
        {
            await auditLogger.LogAsync("ReloadRights", user.UserKey, "app_user", user.UserKey.ToString(), detail: "Self-service Reload My Rights");
        }

        return Ok(rights);
    }
}
