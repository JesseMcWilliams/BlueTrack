using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/safes")]
[Authorize(Policy = Permissions.CurateApplicationMapping)]
public sealed class SafesController(
    ApplicationRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    /// <summary>Every safe, or (D-213) one page of them with the X-Total-Count / X-Filtered-Count headers when page is given.</summary>
    [HttpGet]
    public async Task<IActionResult> GetAll([FromQuery] int? page = null, [FromQuery] int? pageSize = null,
        [FromQuery] string? search = null, [FromQuery] bool unassigned = false)
    {
        if (page is not null)
        {
            Response.Headers["X-Total-Count"] = (await repository.GetSafeCountAsync()).ToString();
            Response.Headers["X-Filtered-Count"] = (await repository.CountSafesAsync(search, unassigned)).ToString();
        }
        return Ok(await repository.GetAllSafesAsync(page, pageSize, search, unassigned));
    }

    [HttpPut("{safeKey:int}/application")]
    public async Task<IActionResult> AssignApplication(int safeKey, [FromBody] int? applicationKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.AssignSafeApplicationAsync(safeKey, applicationKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_safe", safeKey.ToString(),
            detail: $"ApplicationKey set to {(applicationKey?.ToString() ?? "null")}");
        return NoContent();
    }
}
