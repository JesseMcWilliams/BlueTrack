using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

/// <summary>Design_Risk-Scoring.md, D-101-105, Phase A: single add/edit/delete for the Target inventory. Bulk CSV upload/import-template follow in Phase B.</summary>
[ApiController]
[Route("api/admin/targets")]
[Authorize(Policy = Permissions.ManageTargets)]
public sealed class TargetsController(
    TargetRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    /// <summary>D-121: stacked filters (type/application) plus sort, and an X-Total-Count header carrying the unfiltered grand total (the JSON body stays a bare array, unchanged). D-124 Phase 2: the type filter is now the FK key (TargetTypeKey), not the old raw TargetType string. D-124 Phase 3: page/pageSize add server-side paging (2,380 real rows today); X-Filtered-Count carries how many rows match the current filter, ignoring paging.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll(
        [FromQuery] int? targetTypeKey = null,
        [FromQuery] int? applicationKey = null,
        [FromQuery] string? sort = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
    {
        var sortBy = SortParser.Parse(sort);
        var results = await repository.GetAllAsync(targetTypeKey, applicationKey, sortBy, page, pageSize);
        Response.Headers["X-Total-Count"] = (await repository.GetTotalCountAsync()).ToString();
        Response.Headers["X-Filtered-Count"] = (await repository.GetFilteredCountAsync(targetTypeKey, applicationKey)).ToString();
        return Ok(results);
    }

    /// <summary>D-190: "Select all matching" for the list's bulk actions -- no keys if more match than the bulk limit.</summary>
    [HttpGet("keys")]
    public async Task<IActionResult> GetMatchingKeys([FromQuery] int? targetTypeKey, [FromQuery] int? applicationKey,
        [FromServices] BulkActions.BulkActionRunner runner)
    {
        var max = await runner.GetMaxItemsAsync();
        var matching = await repository.GetFilteredCountAsync(targetTypeKey, applicationKey);
        var keys = matching > max ? [] : await repository.GetFilteredKeysAsync(targetTypeKey, applicationKey, max);
        return Ok(new BulkKeysResult { MatchingCount = matching, MaxItems = max, Keys = keys });
    }

    private static readonly HashSet<string> BulkEditableFields = new(StringComparer.OrdinalIgnoreCase)
        { "TargetTypeKey", "ApplicationKey", "RiskScore", "Description" };

    /// <summary>D-190: the same values for the chosen fields on every selected target; each is saved (and audited) as a single edit would be.</summary>
    [HttpPost("bulk-edit")]
    public async Task<IActionResult> BulkEdit([FromBody] BulkEditTargetsRequest request, [FromServices] BulkActions.BulkActionRunner runner)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        var error = await runner.ValidateAsync(request.Keys);
        bool Has(string field) => request.Fields.Contains(field, StringComparer.OrdinalIgnoreCase);
        if (error is null && request.Fields is not { Count: > 0 }) error = "Choose at least one field to change.";
        if (error is null && request.Fields.FirstOrDefault(f => !BulkEditableFields.Contains(f)) is { } unknown) error = $"'{unknown}' can't be bulk edited.";
        if (error is null && Has("TargetTypeKey") && (request.TargetTypeKey is null || !(await repository.GetTargetTypesAsync()).Any(t => t.TargetTypeKey == request.TargetTypeKey))) error = "Choose a valid Target Type.";
        if (error is null && Has("RiskScore") && request.RiskScore is null or < 0) error = "Risk Score must be 0 or more.";
        if (error is not null) return Problem(title: "Invalid bulk edit", detail: error, statusCode: StatusCodes.Status400BadRequest);

        var result = await BulkActions.BulkActionRunner.RunAsync(request.Keys, async key =>
        {
            var before = await repository.GetByKeyAsync(key) ?? throw new BulkActions.BulkActionRunner.SkipException("Target not found.");
            var save = new SaveTargetRequest
            {
                TargetTypeKey = Has("TargetTypeKey") ? request.TargetTypeKey!.Value : before.TargetTypeKey,
                TargetName = before.TargetName,
                ApplicationKey = Has("ApplicationKey") ? request.ApplicationKey : before.ApplicationKey,
                RiskScore = Has("RiskScore") ? request.RiskScore!.Value : before.RiskScore,
                Description = Has("Description") ? (string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim()) : before.Description,
                DiscoverySource = before.DiscoverySource,
                Identifiers = before.Identifiers.Select(i => new SaveTargetIdentifierRequest { IdentifierType = i.IdentifierType, IdentifierValue = i.IdentifierValue }).ToList()
            };
            await repository.UpdateAsync(key, save, user.UserKey);
            List<FieldChange> changes = [];
            void Track(string name, object? oldValue, object? newValue)
            {
                if (oldValue?.ToString() != newValue?.ToString()) changes.Add(new FieldChange(name, oldValue?.ToString(), newValue?.ToString()));
            }
            Track("TargetTypeKey", before.TargetTypeKey, save.TargetTypeKey);
            Track("ApplicationKey", before.ApplicationKey, save.ApplicationKey);
            Track("RiskScore", before.RiskScore, save.RiskScore);
            Track("Description", before.Description, save.Description);
            await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_target", key.ToString(),
                detail: $"Target '{before.TargetName}' updated (bulk edit)", fieldChanges: changes);
            return before.TargetName;
        });
        return Ok(result);
    }

    /// <summary>D-190: deletes the selected targets; a reason is required and recorded on each one's audit event.</summary>
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
            var target = await repository.GetByKeyAsync(key) ?? throw new BulkActions.BulkActionRunner.SkipException("Target not found.");
            try
            {
                await repository.DeleteAsync(key);
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 547)
            {
                throw new BulkActions.BulkActionRunner.SkipException("Access groups or accounts still point at it, so it can't be deleted.", target.TargetName);
            }
            await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_target", key.ToString(),
                detail: $"Target '{target.TargetName}' deleted (bulk delete)", reason: reason);
            return target.TargetName;
        });
        return Ok(result);
    }

    [HttpGet("identifier-types")]
    public async Task<IActionResult> GetIdentifierTypes() => Ok(await repository.GetIdentifierTypesAsync());

    /// <summary>D-124 Phase 2: web.dim_target_type reference data for the Type dropdown, mirroring the identifier-types route above.</summary>
    [HttpGet("target-types")]
    public async Task<IActionResult> GetTargetTypes() => Ok(await repository.GetTargetTypesAsync());

    /// <summary>D-124 Phase 4: backs the new routed Target Edit page, mirroring RiskExceptionsController.GetByKey's shape (a direct-navigation-safe single-row lookup, distinct from the paginated GetAll above).</summary>
    [HttpGet("{targetKey:int}")]
    public async Task<IActionResult> GetByKey(int targetKey)
    {
        var target = await repository.GetByKeyAsync(targetKey);
        if (target is null)
        {
            return NotFound();
        }

        return Ok(target);
    }

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
