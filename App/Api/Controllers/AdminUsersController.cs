using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-14's admin half of Reload Rights: trigger a reload for another user's
/// "active session." With no ASP.NET Core cookie session concept in this
/// app (D-82's per-identity cache stands in for one), "reload for another
/// user" means invalidating their cached rights (UserRightsCache) --
/// their own next request re-resolves live via their own Negotiate token,
/// exactly like self-service Reload Rights does, just triggered by someone
/// else. This deliberately does NOT try to query AD/LDAP directly for the
/// target user's group membership from the admin's own request context --
/// that would need a separate mechanism (e.g. System.DirectoryServices)
/// this app doesn't have, and invalidate-then-let-them-refresh achieves
/// the same live-query outcome (D-13) without it.
/// </summary>
[ApiController]
[Route("api/admin/users")]
[Authorize(Policy = Permissions.ReloadRights)]
public sealed class AdminUsersController(
    AppUserRepository appUserRepository,
    UserRightsCache rightsCache,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    [HttpPost("{userKey:int}/reload-rights")]
    public async Task<IActionResult> ReloadRightsForUser(int userKey)
    {
        var targetUser = await appUserRepository.GetByKeyAsync(userKey);
        if (targetUser is null)
        {
            return NotFound();
        }

        await rightsCache.InvalidateAsync(targetUser.ProviderKey, targetUser.ExternalIdentifier);

        var admin = await currentUserResolver.ResolveAsync(User);
        if (admin is not null)
        {
            await auditLogger.LogAsync("ReloadRights", admin.UserKey, "app_user", userKey.ToString(),
                detail: $"Reload Rights triggered for user {targetUser.DisplayName ?? targetUser.ExternalIdentifier}");
        }

        return NoContent();
    }
}
