using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/admin/configuration")]
[Authorize(Policy = Permissions.ManageApplicationConfiguration)]
public sealed class GlobalApplicationConfigController(
    AppConfigRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get() => Ok(await repository.GetAsync());

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] SaveGlobalApplicationConfigRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var before = await repository.GetAsync();
        await repository.UpdateAsync(request, user.UserKey);

        List<FieldChange> changes = [];
        if (before.IdleTimeoutMinutes != request.IdleTimeoutMinutes)
            changes.Add(new FieldChange("IdleTimeoutMinutes", before.IdleTimeoutMinutes.ToString(), request.IdleTimeoutMinutes.ToString()));
        if (before.BreadcrumbPosition != request.BreadcrumbPosition)
            changes.Add(new FieldChange("BreadcrumbPosition", before.BreadcrumbPosition, request.BreadcrumbPosition));
        if (before.ExceptionIdPattern != request.ExceptionIdPattern)
            changes.Add(new FieldChange("ExceptionIdPattern", before.ExceptionIdPattern, request.ExceptionIdPattern));
        if (before.LockTimeoutMinutes != request.LockTimeoutMinutes)
            changes.Add(new FieldChange("LockTimeoutMinutes", before.LockTimeoutMinutes.ToString(), request.LockTimeoutMinutes.ToString()));
        if (before.RetentionDays != request.RetentionDays)
            changes.Add(new FieldChange("RetentionDays", before.RetentionDays?.ToString(), request.RetentionDays?.ToString()));
        if (before.LogReadEvents != request.LogReadEvents)
            changes.Add(new FieldChange("LogReadEvents", before.LogReadEvents.ToString(), request.LogReadEvents.ToString()));

        if (changes.Count > 0)
        {
            await auditLogger.LogAsync("FieldEdit", user.UserKey, "app_config", entityKey: null, fieldChanges: changes);
        }

        return NoContent();
    }
}
