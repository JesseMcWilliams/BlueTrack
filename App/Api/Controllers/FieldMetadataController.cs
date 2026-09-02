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
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repository.GetAllAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveFieldMetadataRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

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

        await repository.UpdateAsync(fieldMetadataKey, request);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "account_progress_field_metadata", fieldMetadataKey.ToString(),
            detail: $"Field metadata '{request.FieldName}' updated");
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
