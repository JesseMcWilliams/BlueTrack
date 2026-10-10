using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

/// <summary>Design_Risk-Scoring.md, D-101-105, Phase A: single add/edit/delete for Access Groups. Bulk CSV upload follows in Phase B.</summary>
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

    /// <summary>D-190: "Select all matching" for the list's bulk actions -- no keys if more match than the bulk limit.</summary>
    [HttpGet("keys")]
    public async Task<IActionResult> GetMatchingKeys([FromQuery] string? groupScope, [FromQuery] string? sorTypeName,
        [FromServices] BulkActions.BulkActionRunner runner)
    {
        var max = await runner.GetMaxItemsAsync();
        var matching = await repository.GetFilteredCountAsync(groupScope, sorTypeName);
        var keys = matching > max ? [] : await repository.GetFilteredKeysAsync(groupScope, sorTypeName, max);
        return Ok(new BulkKeysResult { MatchingCount = matching, MaxItems = max, Keys = keys });
    }

    private static readonly HashSet<string> BulkEditableFields = new(StringComparer.OrdinalIgnoreCase)
        { "GroupScope", "BaseRiskScore", "Description" };

    /// <summary>
    /// D-190: the same values for the chosen fields on every selected access
    /// group, each saved (and audited) as a single edit would be. Switching to
    /// Local scope needs the target the group was found on, as on the edit
    /// page, so a group without one is skipped.
    /// </summary>
    [HttpPost("bulk-edit")]
    public async Task<IActionResult> BulkEdit([FromBody] BulkEditAccessGroupsRequest request, [FromServices] BulkActions.BulkActionRunner runner)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        var error = await runner.ValidateAsync(request.Keys);
        bool Has(string field) => request.Fields.Contains(field, StringComparer.OrdinalIgnoreCase);
        if (error is null && request.Fields is not { Count: > 0 }) error = "Choose at least one field to change.";
        if (error is null && request.Fields.FirstOrDefault(f => !BulkEditableFields.Contains(f)) is { } unknown) error = $"'{unknown}' can't be bulk edited.";
        if (error is null && Has("GroupScope") && request.GroupScope is not ("Domain" or "Local")) error = "Scope must be Domain or Local.";
        if (error is null && Has("BaseRiskScore") && request.BaseRiskScore is null or < 0) error = "Base Risk Score must be 0 or more.";
        if (error is not null) return Problem(title: "Invalid bulk edit", detail: error, statusCode: StatusCodes.Status400BadRequest);

        var result = await BulkActions.BulkActionRunner.RunAsync(request.Keys, async key =>
        {
            var before = await repository.GetByKeyAsync(key) ?? throw new BulkActions.BulkActionRunner.SkipException("Access group not found.");
            var scope = Has("GroupScope") ? request.GroupScope! : before.GroupScope;
            if (scope == "Local" && before.FoundOnTargetKey is null)
            {
                throw new BulkActions.BulkActionRunner.SkipException("Local scope needs the target it was found on; set it on the group's own page.", before.GroupName);
            }
            var save = new SaveAccessGroupRequest
            {
                GroupName = before.GroupName,
                GroupIdentifier = before.GroupIdentifier,
                GroupScope = scope,
                FoundOnTargetKey = before.FoundOnTargetKey,
                DiscoverySource = before.DiscoverySource,
                SorTypeKey = before.SorTypeKey,
                SorAddress = before.SorAddress,
                BaseRiskScore = Has("BaseRiskScore") ? request.BaseRiskScore!.Value : before.BaseRiskScore,
                Description = Has("Description") ? (string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim()) : before.Description
            };
            await repository.UpdateAsync(key, save, user.UserKey);
            List<FieldChange> changes = [];
            void Track(string name, object? oldValue, object? newValue)
            {
                if (oldValue?.ToString() != newValue?.ToString()) changes.Add(new FieldChange(name, oldValue?.ToString(), newValue?.ToString()));
            }
            Track("GroupScope", before.GroupScope, save.GroupScope);
            Track("BaseRiskScore", before.BaseRiskScore, save.BaseRiskScore);
            Track("Description", before.Description, save.Description);
            await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_access_group", key.ToString(),
                detail: $"Access Group '{before.GroupName}' updated (bulk edit)", fieldChanges: changes);
            return before.GroupName;
        });
        return Ok(result);
    }

    /// <summary>D-190: deletes the selected access groups; a reason is required and recorded on each one's audit event.</summary>
    [HttpPost("bulk-delete")]
    public async Task<IActionResult> BulkDelete([FromBody] BulkDeleteRequest request, [FromServices] BulkActions.BulkActionRunner runner)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        if (await runner.ValidateAsync(request.Keys, reasonRequired: true, request.Reason) is { } error)
        {
            return Problem(title: "Invalid bulk delete", detail: error, statusCode: StatusCodes.Status400BadRequest);
        }

        var reason = request.Reason.Trim();
        var result = await BulkActions.BulkActionRunner.RunAsync(request.Keys, async key =>
        {
            var group = await repository.GetByKeyAsync(key) ?? throw new BulkActions.BulkActionRunner.SkipException("Access group not found.");
            try
            {
                await repository.DeleteAsync(key);
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 547)
            {
                throw new BulkActions.BulkActionRunner.SkipException("Accounts or targets are still mapped to it, so it can't be deleted.", group.GroupName);
            }
            await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_access_group", key.ToString(),
                detail: $"Access Group '{group.GroupName}' deleted (bulk delete)", reason: reason);
            return group.GroupName;
        });
        return Ok(result);
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
