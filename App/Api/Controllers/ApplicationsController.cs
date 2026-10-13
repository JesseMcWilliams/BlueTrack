using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// GET is used by the Risk Exception create form's application-scoping
/// dropdown (any authenticated user); the write actions back the
/// Application ↔ Safe Mapping admin page and require CurateApplicationMapping.
/// </summary>
[ApiController]
[Route("api/applications")]
[Authorize]
public sealed class ApplicationsController(
    ApplicationRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    [Authorize(Policy = Permissions.ViewDashboard)]   // D-197: reading tracked data needs ViewDashboard, not just a sign-in
    [HttpGet]
    public async Task<IActionResult> GetList() => Ok(await repository.GetAllAsync());

    /// <summary>Every application, or (D-213) one page of them with the X-Total-Count / X-Filtered-Count headers when page is given.</summary>
    [HttpGet("detailed")]
    [Authorize(Policy = Permissions.CurateApplicationMapping)]
    public async Task<IActionResult> GetAllDetailed([FromQuery] int? page = null, [FromQuery] int? pageSize = null)
    {
        if (page is not null)
        {
            var count = (await repository.GetCountAsync()).ToString();
            Response.Headers["X-Total-Count"] = count;
            Response.Headers["X-Filtered-Count"] = count;
        }
        return Ok(await repository.GetAllDetailedAsync(page, pageSize));
    }

    [HttpPost]
    [Authorize(Policy = Permissions.CurateApplicationMapping)]
    public async Task<IActionResult> Create([FromBody] SaveApplicationRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var key = await repository.CreateAsync(request);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_application", key.ToString(),
            detail: $"Application '{request.ApplicationName}' created");
        return CreatedAtAction(nameof(GetAllDetailed), new { }, new { applicationKey = key });
    }

    [HttpPut("{applicationKey:int}")]
    [Authorize(Policy = Permissions.CurateApplicationMapping)]
    public async Task<IActionResult> Update(int applicationKey, [FromBody] SaveApplicationRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.UpdateAsync(applicationKey, request);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_application", applicationKey.ToString(),
            detail: $"Application '{request.ApplicationName}' updated");
        return NoContent();
    }
}
