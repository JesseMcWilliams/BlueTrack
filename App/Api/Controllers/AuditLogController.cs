using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/audit-log")]
[Authorize(Policy = Permissions.ViewAuditLog)]
public sealed class AuditLogController(AuditRepository repository) : ControllerBase
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

    [HttpGet("{auditEventKey:long}/field-changes")]
    public async Task<IActionResult> GetFieldChanges(long auditEventKey)
    {
        return Ok(await repository.GetFieldChangesAsync(auditEventKey));
    }
}
