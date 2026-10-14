using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/admin/field-metadata")]
[Authorize(Policy = Permissions.ManageFieldMetadata)]
public sealed class FieldMetadataController(
    FieldMetadataRepository repository,
    RoleRepository roleRepository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repository.GetAllAsync());

    /// <summary>D-214: the permissions a field's Required Permission can name (D-198), for the Edit Field page.</summary>
    [HttpGet("permission-options")]
    public async Task<IActionResult> GetPermissionOptions() => Ok(await roleRepository.GetPermissionCatalogAsync());

    private async Task<IActionResult?> ValidateRequiredPermissionAsync(SaveFieldMetadataRequest request)
    {
        if (request.RequiredPermission is not { } key) return null;
        return (await roleRepository.GetPermissionCatalogAsync()).Any(p => p.PermissionKey == key)
            ? null
            : Problem(title: "Invalid field metadata", detail: "Required Permission isn't a known permission.", statusCode: StatusCodes.Status400BadRequest);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveFieldMetadataRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        if (await ValidateRequiredPermissionAsync(request) is { } problem) return problem;

        var key = await repository.CreateAsync(request);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "account_progress_field_metadata", key.ToString(),
            detail: $"Field metadata '{request.FieldName}' created");
        return CreatedAtAction(nameof(GetAll), new { }, new { fieldMetadataKey = key });
    }

    [HttpPut("{fieldMetadataKey:int}")]
    public async Task<IActionResult> Update(int fieldMetadataKey, [FromBody] SaveFieldMetadataRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        if (await ValidateRequiredPermissionAsync(request) is { } problem) return problem;

        var before = (await repository.GetAllAsync()).FirstOrDefault(f => f.FieldMetadataKey == fieldMetadataKey);
        await repository.UpdateAsync(fieldMetadataKey, request);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "account_progress_field_metadata", fieldMetadataKey.ToString(),
            detail: $"Field metadata '{request.FieldName}' updated",
            fieldChanges: before?.RequiredPermission == request.RequiredPermission ? null :
                [new FieldChange("RequiredPermission", before?.RequiredPermission?.ToString(), request.RequiredPermission?.ToString())]);
        return NoContent();
    }

    [HttpDelete("{fieldMetadataKey:int}")]
    public async Task<IActionResult> Delete(int fieldMetadataKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.DeleteAsync(fieldMetadataKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "account_progress_field_metadata", fieldMetadataKey.ToString(),
            detail: "Field metadata deleted");
        return NoContent();
    }
}
