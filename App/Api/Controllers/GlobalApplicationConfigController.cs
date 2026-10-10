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
    AuditLogger auditLogger,
    DecommissionRepository decommissionRepository) : ControllerBase
{
    /// <summary>
    /// D-186: the settings page's pattern helper -- which sample names each
    /// (unsaved) pattern matches, using the same SQL function as the
    /// reports and the nightly import. A pattern that can't be used reports
    /// why and matches nothing.
    /// </summary>
    [HttpPost("test-patterns")]
    public async Task<IActionResult> TestPatterns([FromBody] PatternTestRequest request)
    {
        if (request.Names.Count > 200) return BadRequest(new { error = "Test at most 200 names at once." });
        var regexSupported = await decommissionRepository.IsRegexSupportedAsync();
        var errors = new Dictionary<string, string>();
        async Task<NamePattern> Checked(string key, NamePattern pattern)
        {
            if (await decommissionRepository.ValidateAsync(pattern, regexSupported) is not { } error) return pattern;
            errors[key] = error;
            return new NamePattern { Mode = "Off" };
        }
        var safeDecom = await Checked("SafeDecom", request.SafeDecom);
        var accountDecom = await Checked("AccountDecom", request.AccountDecom);
        var safeIgnore = await Checked("SafeIgnore", request.SafeIgnore);
        var names = request.Names.Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        var rows = await decommissionRepository.TestAsync(names, safeDecom, accountDecom, safeIgnore);
        return Ok(new PatternTestResult { Rows = rows, Errors = errors });
    }

    [HttpGet]
    public async Task<IActionResult> Get() => Ok(await repository.GetAsync());

    [HttpPut]
    public async Task<IActionResult> Update([FromBody] SaveGlobalApplicationConfigRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        // D-181: the data-feed schedule settings are checked here, not left to
        // fail as a SQL CAST error.
        var scheduleErrors = DataFeeds.FeedSchedule.Validate(request.DataFeedRunTime, request.DataFeedRunRetentionDays,
            request.BusinessHoursStart, request.BusinessHoursEnd, request.BusinessDays);
        // D-186: each pattern sent must be usable (a bad regex would break the
        // reports and the nightly import, which use it).
        var regexSupported = await decommissionRepository.IsRegexSupportedAsync();
        foreach (var (label, mode, value) in new[]
        {
            ("Safe decommission pattern", request.SafeDecomMode, request.SafeDecomValue),
            ("Account decommission pattern", request.AccountDecomMode, request.AccountDecomValue),
            ("Ignored safe pattern", request.SafeIgnoreMode, request.SafeIgnoreValue)
        })
        {
            if (mode is null) continue;
            if (await decommissionRepository.ValidateAsync(new NamePattern { Mode = mode, Value = value }, regexSupported) is { } patternError)
            {
                return Problem(title: "Invalid name pattern", detail: $"{label}: {patternError}", statusCode: StatusCodes.Status400BadRequest);
            }
        }
        if (request.BulkEditMaxAccounts is < 1 or > 10000)
        {
            return Problem(title: "Invalid bulk edit limit", detail: "BulkEditMaxAccounts must be between 1 and 10000.", statusCode: StatusCodes.Status400BadRequest);
        }
        if (scheduleErrors.Count > 0)
        {
            return Problem(title: "Invalid data feed schedule settings", detail: string.Join(" ", scheduleErrors), statusCode: StatusCodes.Status400BadRequest);
        }

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
        if (before.BackupFolder != request.BackupFolder)
            changes.Add(new FieldChange("BackupFolder", before.BackupFolder, request.BackupFolder));
        if (before.ActiveRiskAlgorithm != request.ActiveRiskAlgorithm)
            changes.Add(new FieldChange("ActiveRiskAlgorithm", before.ActiveRiskAlgorithm, request.ActiveRiskAlgorithm));
        if (before.EnforceRiskExceptionSegregationOfDuties != request.EnforceRiskExceptionSegregationOfDuties)
            changes.Add(new FieldChange("EnforceRiskExceptionSegregationOfDuties", before.EnforceRiskExceptionSegregationOfDuties.ToString(), request.EnforceRiskExceptionSegregationOfDuties.ToString()));

        void TrackIfSent(string field, string? beforeValue, string? newValue)
        {
            if (newValue is not null && newValue != beforeValue) changes.Add(new FieldChange(field, beforeValue, newValue));
        }
        TrackIfSent("DataFeedRunTime", before.DataFeedRunTime, request.DataFeedRunTime);
        TrackIfSent("DataFeedRunRetentionDays", before.DataFeedRunRetentionDays.ToString(), request.DataFeedRunRetentionDays?.ToString());
        TrackIfSent("BusinessHoursStart", before.BusinessHoursStart, request.BusinessHoursStart);
        TrackIfSent("BusinessHoursEnd", before.BusinessHoursEnd, request.BusinessHoursEnd);
        TrackIfSent("BusinessDays", before.BusinessDays, request.BusinessDays);
        TrackIfSent("BulkEditMaxAccounts", before.BulkEditMaxAccounts.ToString(), request.BulkEditMaxAccounts?.ToString());
        static string? Pattern(string? mode, string? value) => mode is null ? null : mode == "Off" ? "Off" : $"{mode}: {value}";
        TrackIfSent("SafeDecomPattern", Pattern(before.SafeDecomMode, before.SafeDecomValue), Pattern(request.SafeDecomMode, request.SafeDecomValue));
        TrackIfSent("AccountDecomPattern", Pattern(before.AccountDecomMode, before.AccountDecomValue), Pattern(request.AccountDecomMode, request.AccountDecomValue));
        TrackIfSent("SafeIgnorePattern", Pattern(before.SafeIgnoreMode, before.SafeIgnoreValue), Pattern(request.SafeIgnoreMode, request.SafeIgnoreValue));

        if (changes.Count > 0)
        {
            await auditLogger.LogAsync("FieldEdit", user.UserKey, "app_config", entityKey: null, fieldChanges: changes);
        }

        return NoContent();
    }
}
