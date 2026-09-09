using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

/// <summary>Design_Risk_Scoring.md, D-101-105, Phase A: single add/edit/delete for the Target inventory. Bulk CSV upload/import-template follow in Phase B.</summary>
[ApiController]
[Route("api/admin/targets")]
[Authorize(Policy = Permissions.ManageTargets)]
public sealed class TargetsController(
    TargetRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    /// <summary>D-121: stacked filters (type/application) plus sort, and an X-Total-Count header carrying the unfiltered grand total (the JSON body stays a bare array, unchanged).</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? targetType = null,
        [FromQuery] int? applicationKey = null,
        [FromQuery] string? sort = null)
    {
        var sortBy = SortParser.Parse(sort);
        var results = await repository.GetAllAsync(targetType, applicationKey, sortBy);
        Response.Headers["X-Total-Count"] = (await repository.GetTotalCountAsync()).ToString();
        return Ok(results);
    }

    [HttpGet("identifier-types")]
    public async Task<IActionResult> GetIdentifierTypes() => Ok(await repository.GetIdentifierTypesAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveTargetRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var key = await repository.CreateAsync(request, user.UserKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_target", key.ToString(), detail: $"Target '{request.TargetName}' created");
        return CreatedAtAction(nameof(GetAll), new { }, new { targetKey = key });
    }

    [HttpPut("{targetKey:int}")]
    public async Task<IActionResult> Update(int targetKey, [FromBody] SaveTargetRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.UpdateAsync(targetKey, request, user.UserKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_target", targetKey.ToString(), detail: $"Target '{request.TargetName}' updated");
        return NoContent();
    }

    [HttpDelete("{targetKey:int}")]
    public async Task<IActionResult> Delete(int targetKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.DeleteAsync(targetKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_target", targetKey.ToString(), detail: "Target deleted");
        return NoContent();
    }
}
