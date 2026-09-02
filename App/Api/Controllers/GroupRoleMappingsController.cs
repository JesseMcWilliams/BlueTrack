using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/admin/group-role-mappings")]
[Authorize(Policy = Permissions.ManageGroupRoleMapping)]
public sealed class GroupRoleMappingsController(
    GroupRoleMappingRepository repository,
    IdentityProviderRepository identityProviderRepository,
    AuthorizationRepository authorizationRepository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repository.GetAllAsync());

    /// <summary>
    /// The lookup/test tool (Design_Authorization_Model.md): resolves a
    /// friendly group name to its SID and shows what role(s)/permissions
    /// it currently resolves to, without saving anything.
    /// </summary>
    [HttpPost("resolve-group")]
    public async Task<IActionResult> ResolveGroup([FromBody] ResolveGroupRequest request)
    {
        var resolved = WindowsGroupResolver.TryResolve(request.GroupName);
        if (resolved is null)
        {
            return Problem(title: "Group not found", detail: $"Could not resolve '{request.GroupName}' to a Windows account.", statusCode: StatusCodes.Status404NotFound);
        }

        var provider = await identityProviderRepository.GetByTypeAsync("WindowsIntegrated");
        if (provider is null)
        {
            return Problem(title: "No WindowsIntegrated provider configured", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var (sid, resolvedAccountName) = resolved.Value;
        var roleNames = await authorizationRepository.GetMatchedRoleNamesAsync(provider.ProviderKey, [sid]);
        var permissionNames = await authorizationRepository.GetEffectivePermissionNamesAsync(provider.ProviderKey, [sid]);

        return Ok(new ResolveGroupResult
        {
            ResolvedAccountName = resolvedAccountName,
            Sid = sid,
            CurrentRoleNames = roleNames,
            CurrentPermissionNames = permissionNames
        });
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateGroupRoleMappingRequest request)
    {
        var resolved = WindowsGroupResolver.TryResolve(request.GroupName);
        if (resolved is null)
        {
            return Problem(title: "Group not found", detail: $"Could not resolve '{request.GroupName}' to a Windows account.", statusCode: StatusCodes.Status404NotFound);
        }

        var provider = await identityProviderRepository.GetByTypeAsync("WindowsIntegrated");
        if (provider is null)
        {
            return Problem(title: "No WindowsIntegrated provider configured", statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var roleKey = await repository.GetRoleKeyByNameAsync(request.RoleName);
        if (roleKey is null)
        {
            return Problem(title: "Role not found", detail: $"No role named '{request.RoleName}'.", statusCode: StatusCodes.Status400BadRequest);
        }

        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var (sid, resolvedAccountName) = resolved.Value;
        var mappingKey = await repository.CreateAsync(provider.ProviderKey, sid, roleKey.Value);

        await auditLogger.LogAsync("FieldEdit", user.UserKey, "identity_group_role_map", mappingKey.ToString(),
            detail: $"Mapped {resolvedAccountName} ({sid}) to role '{request.RoleName}'");

        return CreatedAtAction(nameof(GetAll), new { }, new { mappingKey, sid, resolvedAccountName });
    }

    [HttpDelete("{mappingKey:int}")]
    public async Task<IActionResult> Delete(int mappingKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.DeleteAsync(mappingKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "identity_group_role_map", mappingKey.ToString(), detail: "Mapping deleted");
        return NoContent();
    }
}
