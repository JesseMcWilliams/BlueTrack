using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

/// <summary>Design_Risk_Scoring.md, D-101-105, Phase A: single add/edit/delete for Access Groups. Bulk CSV upload follows in Phase B.</summary>
[ApiController]
[Route("api/admin/access-groups")]
[Authorize(Policy = Permissions.ManageAccessGroups)]
public sealed class AccessGroupsController(
    AccessGroupRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    /// <summary>D-121: stacked filters (scope/SOR type) plus sort, and an X-Total-Count header carrying the unfiltered grand total (the JSON body stays a bare array, unchanged). D-124 Phase 3: page/pageSize add server-side paging; X-Filtered-Count carries how many rows match the current filter, ignoring paging.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] string? groupScope = null,
        [FromQuery] string? sorTypeName = null,
        [FromQuery] string? sort = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
    {
        var sortBy = SortParser.Parse(sort);
        var results = await repository.GetAllAsync(groupScope, sorTypeName, sortBy, page, pageSize);
        Response.Headers["X-Total-Count"] = (await repository.GetTotalCountAsync()).ToString();
        Response.Headers["X-Filtered-Count"] = (await repository.GetFilteredCountAsync(groupScope, sorTypeName)).ToString();
        return Ok(results);
    }

    [HttpGet("sor-types")]
    public async Task<IActionResult> GetSorTypes() => Ok(await repository.GetSorTypesAsync());

    /// <summary>D-124 Phase 4: backs the new routed Access Group Edit page, mirroring RiskExceptionsController.GetByKey's shape (a direct-navigation-safe single-row lookup, distinct from the paginated GetAll above).</summary>
    [HttpGet("{accessGroupKey:int}")]
    public async Task<IActionResult> GetByKey(int accessGroupKey)
    {
        var group = await repository.GetByKeyAsync(accessGroupKey);
        if (group is null)
        {
            return NotFound();
        }

        return Ok(group);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveAccessGroupRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var key = await repository.CreateAsync(request, user.UserKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_access_group", key.ToString(), detail: $"Access Group '{request.GroupName}' created");
        return CreatedAtAction(nameof(GetAll), new { }, new { accessGroupKey = key });
    }

    [HttpPut("{accessGroupKey:int}")]
    public async Task<IActionResult> Update(int accessGroupKey, [FromBody] SaveAccessGroupRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.UpdateAsync(accessGroupKey, request, user.UserKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_access_group", accessGroupKey.ToString(), detail: $"Access Group '{request.GroupName}' updated");
        return NoContent();
    }

    [HttpDelete("{accessGroupKey:int}")]
    public async Task<IActionResult> Delete(int accessGroupKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.DeleteAsync(accessGroupKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_access_group", accessGroupKey.ToString(), detail: "Access Group deleted");
        return NoContent();
    }
}
