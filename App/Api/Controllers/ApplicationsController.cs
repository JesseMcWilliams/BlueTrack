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
    [HttpGet]
    public async Task<IActionResult> GetList() => Ok(await repository.GetAllAsync());

    [HttpGet("detailed")]
    [Authorize(Policy = Permissions.CurateApplicationMapping)]
    public async Task<IActionResult> GetAllDetailed() => Ok(await repository.GetAllDetailedAsync());

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
