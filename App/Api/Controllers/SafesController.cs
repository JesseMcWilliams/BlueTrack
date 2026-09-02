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
    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repository.GetAllSafesAsync());

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
