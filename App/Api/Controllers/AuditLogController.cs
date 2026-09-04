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
    [HttpGet]
    public async Task<IActionResult> GetEvents(
        [FromQuery] string? eventType = null,
        [FromQuery] string? entityName = null,
        [FromQuery] int? performedByUserKey = null,
        [FromQuery] DateTime? fromDate = null,
        [FromQuery] DateTime? toDate = null,
        [FromQuery] string? sort = null)
    {
        var sortBy = SortParser.Parse(sort);
        return Ok(await repository.GetEventsAsync(eventType, entityName, performedByUserKey, fromDate, toDate, sortBy));
    }

    [HttpGet("{auditEventKey:long}/field-changes")]
    public async Task<IActionResult> GetFieldChanges(long auditEventKey)
    {
        return Ok(await repository.GetFieldChangesAsync(auditEventKey));
    }
}
