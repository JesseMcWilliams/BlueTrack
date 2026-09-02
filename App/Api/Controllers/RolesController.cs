using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/admin")]
[Authorize(Policy = Permissions.ManageRolesAndPermissions)]
public sealed class RolesController(
    RoleRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    [HttpGet("permissions")]
    public async Task<IActionResult> GetPermissionCatalog() => Ok(await repository.GetPermissionCatalogAsync());

    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles() => Ok(await repository.GetRolesAsync());

    [HttpPost("roles")]
    public async Task<IActionResult> CreateRole([FromBody] SaveRoleRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var roleKey = await repository.CreateRoleAsync(request);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "app_role", roleKey.ToString(),
            detail: $"Role '{request.RoleName}' created with permissions: {string.Join(", ", request.PermissionNames)}");
        return CreatedAtAction(nameof(GetRoles), new { }, new { roleKey });
    }

    [HttpPut("roles/{roleKey:int}")]
    public async Task<IActionResult> UpdateRole(int roleKey, [FromBody] SaveRoleRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.UpdateRoleAsync(roleKey, request);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "app_role", roleKey.ToString(),
            detail: $"Role '{request.RoleName}' updated, permissions now: {string.Join(", ", request.PermissionNames)}");
        return NoContent();
    }

    [HttpDelete("roles/{roleKey:int}")]
    public async Task<IActionResult> DeleteRole(int roleKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.DeleteRoleAsync(roleKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "app_role", roleKey.ToString(), detail: "Role deleted");
        return NoContent();
    }
}
