using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
using System.Globalization;
using BlueTrack.Api.Data;
using BlueTrack.Api.Reports;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/audit-log")]
[Authorize(Policy = Permissions.ViewAuditLog)]
public sealed class AuditLogController(AuditRepository repository, ReportExportService exportService) : ControllerBase
{
    /// <summary>D-124 Phase 3: page/pageSize add server-side paging; X-Filtered-Count carries how many rows match the current filter, ignoring paging.</summary>
    [HttpGet]
    public async Task<IActionResult> GetEvents(
        [FromQuery] string? eventType = null,
        [FromQuery] string? entityName = null,
        [FromQuery] int? performedByUserKey = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] string? sort = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null)
    {
        var sortBy = SortParser.Parse(sort);
        var results = await repository.GetEventsAsync(eventType, entityName, performedByUserKey, fromDate, toDate, sortBy, page, pageSize);
        Response.Headers["X-Total-Count"] = (await repository.GetTotalCountAsync()).ToString();
        Response.Headers["X-Filtered-Count"] = (await repository.GetFilteredCountAsync(eventType, entityName, performedByUserKey, fromDate, toDate)).ToString();
        return Ok(results);
    }

    /// <summary>UI review W11-1: people in the log whose name contains <c>q</c>, for the viewer's By choice (at most 10).</summary>
    [HttpGet("performers")]
    public async Task<IActionResult> GetPerformers([FromQuery] string? q = null) => Ok(await repository.GetPerformersAsync(q));

    /// <summary>UI review W11-1: the event types the log can hold, so the viewer offers a choice instead of free text.</summary>
    [HttpGet("event-types")]
    public async Task<IActionResult> GetEventTypes() => Ok(await repository.GetEventTypeNamesAsync());

    /// <summary>
    /// D-231: the viewer's Export CSV / Export Excel (D-220's shared export): every
    /// event the filters match (all pages) in the viewer's sort order, refused above
    /// Export Max Rows, logged as a ReportExport event. Same ViewAuditLog policy.
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> ExportEvents(
        [FromQuery] string? format,
        [FromQuery] string? eventType = null,
        [FromQuery] string? entityName = null,
        [FromQuery] int? performedByUserKey = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] string? sort = null)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["eventType"] = eventType,
            ["entityName"] = entityName,
            ["performedByUserKey"] = performedByUserKey?.ToString(CultureInfo.InvariantCulture),
            ["fromDate"] = fromDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["toDate"] = toDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            ["sort"] = sort
        };
        return await exportService.ExportAsync(this, ReportExports.AuditLog, format, parameters,
            await repository.GetFilteredCountAsync(eventType, entityName, performedByUserKey, fromDate, toDate),
            async limit => ReportTable.From(ReportExports.AuditLogColumns,
                await repository.GetExportRowsAsync(eventType, entityName, performedByUserKey, fromDate, toDate, SortParser.Parse(sort), limit)));
    }

    [HttpGet("{auditEventKey:long}/field-changes")]
    public async Task<IActionResult> GetFieldChanges(long auditEventKey)
    {
        return Ok(await repository.GetFieldChangesAsync(auditEventKey));
    }
}
