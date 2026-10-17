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
    DecommissionRepository decommissionRepository,
    Security.AllowedFileRoots allowedFileRoots,
    Microsoft.Extensions.Caching.Memory.IMemoryCache memoryCache) : ControllerBase
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
        // At 0 every edit lock reads as abandoned the moment it's taken, so locks
        // fail at random (seen as AccountDeletionTests' intermittent "1 of 2").
        // D-209: Idle Timeout signs people out now; 0 would sign everyone out at once.
        if (request.IdleTimeoutMinutes is < 5 or > 720)
        {
            return Problem(title: "Invalid idle timeout", detail: "IdleTimeoutMinutes must be between 5 and 720.", statusCode: StatusCodes.Status400BadRequest);
        }
        if (request.LockTimeoutMinutes is < 1 or > 720)
        {
            return Problem(title: "Invalid lock timeout", detail: "LockTimeoutMinutes must be between 1 and 720.", statusCode: StatusCodes.Status400BadRequest);
        }
        if (request.BulkEditMaxAccounts is < 1 or > 10000)
        {
            return Problem(title: "Invalid bulk edit limit", detail: "BulkEditMaxAccounts must be between 1 and 10000.", statusCode: StatusCodes.Status400BadRequest);
        }
        // D-220: an .xlsx sheet holds 1,048,576 rows; the column's CHECK matches this range.
        if (request.ExportMaxRows is < 1 or > 1000000)
        {
            return Problem(title: "Invalid export limit", detail: "ExportMaxRows must be between 1 and 1000000.", statusCode: StatusCodes.Status400BadRequest);
        }
        // D-243: a new API key's default expiry; the column's CHECK matches this range.
        if (request.ApiKeyDefaultLifetimeDays is < 1 or > ApiAccess.ApiAccessRules.MaxLifetimeDays)
        {
            return Problem(title: "Invalid API key lifetime", detail: $"ApiKeyDefaultLifetimeDays must be between 1 and {ApiAccess.ApiAccessRules.MaxLifetimeDays}.", statusCode: StatusCodes.Status400BadRequest);
        }
        if (request.ApiRateLimitPerMinute is < 1 or > 100000)
        {
            return Problem(title: "Invalid API rate limit", detail: "ApiRateLimitPerMinute must be between 1 and 100000.", statusCode: StatusCodes.Status400BadRequest);
        }
        if (request.ApiKeyReminderLeadDays is < 1 or > 90)
        {
            return Problem(title: "Invalid API key reminder lead time", detail: "ApiKeyReminderLeadDays must be between 1 and 90.", statusCode: StatusCodes.Status400BadRequest);
        }
        if (request.ApiKeyExpiredRetentionDays is < 0 or > 3650)
        {
            return Problem(title: "Invalid API key retention", detail: "ApiKeyExpiredRetentionDays must be between 0 and 3650.", statusCode: StatusCodes.Status400BadRequest);
        }
        // D-200: scheduled backups -- a valid time (or "" for off) and 1..3650 days.
        if (!string.IsNullOrEmpty(request.ScheduledBackupTime) && !DataFeeds.FeedSchedule.TryParseTime(request.ScheduledBackupTime, out _))
        {
            return Problem(title: "Invalid scheduled backup time", detail: "ScheduledBackupTime must be a time as HH:mm (24-hour), or empty for off.", statusCode: StatusCodes.Status400BadRequest);
        }
        if (request.ScheduledBackupRetentionDays is < 1 or > 3650)
        {
            return Problem(title: "Invalid backup retention", detail: "ScheduledBackupRetentionDays must be between 1 and 3650.", statusCode: StatusCodes.Status400BadRequest);
        }
        var backupFolderAfter = request.BackupFolder;
        if (!string.IsNullOrEmpty(request.ScheduledBackupTime) && string.IsNullOrWhiteSpace(backupFolderAfter))
        {
            return Problem(title: "Backup folder needed", detail: "Set a Backup Folder before scheduling backups.", statusCode: StatusCodes.Status400BadRequest);
        }

        // D-197: SQL Server writes a full database backup to this folder.
        if (!string.IsNullOrWhiteSpace(request.BackupFolder) && allowedFileRoots.Check(request.BackupFolder) is { } backupFolderError)
        {
            return Problem(title: "Invalid backup folder", detail: backupFolderError, statusCode: StatusCodes.Status400BadRequest);
        }
        // D-224: the address the emails link to. Stored normalized; "" = not set.
        if (request.SiteUrl is not null)
        {
            if (!Notifications.SiteLinks.TryNormalize(request.SiteUrl, out var siteUrl, out var siteUrlError))
            {
                return Problem(title: "Invalid site URL", detail: siteUrlError, statusCode: StatusCodes.Status400BadRequest);
            }
            request.SiteUrl = siteUrl ?? "";
        }
        if (scheduleErrors.Count > 0)
        {
            return Problem(title: "Invalid data feed schedule settings", detail: string.Join(" ", scheduleErrors), statusCode: StatusCodes.Status400BadRequest);
        }

        var before = await repository.GetAsync();
        await repository.UpdateAsync(request, user.UserKey);
        memoryCache.Remove(ApiAccess.ApiTokenAuthenticationMiddleware.RateLimitCacheKey);   // D-243: the next request reads the new limit

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
        TrackIfSent("ScheduledBackupTime", before.ScheduledBackupTime ?? "", request.ScheduledBackupTime);
        TrackIfSent("ScheduledBackupRetentionDays", before.ScheduledBackupRetentionDays.ToString(), request.ScheduledBackupRetentionDays?.ToString());
        TrackIfSent("BusinessHoursStart", before.BusinessHoursStart, request.BusinessHoursStart);
        TrackIfSent("BusinessHoursEnd", before.BusinessHoursEnd, request.BusinessHoursEnd);
        TrackIfSent("BusinessDays", before.BusinessDays, request.BusinessDays);
        TrackIfSent("BulkEditMaxAccounts", before.BulkEditMaxAccounts.ToString(), request.BulkEditMaxAccounts?.ToString());
        TrackIfSent("ExportMaxRows", before.ExportMaxRows.ToString(), request.ExportMaxRows?.ToString());
        TrackIfSent("ApiKeyDefaultLifetimeDays", before.ApiKeyDefaultLifetimeDays.ToString(), request.ApiKeyDefaultLifetimeDays?.ToString());   // D-243
        TrackIfSent("ApiRateLimitPerMinute", before.ApiRateLimitPerMinute.ToString(), request.ApiRateLimitPerMinute?.ToString());   // D-243
        TrackIfSent("ApiKeyReminderLeadDays", before.ApiKeyReminderLeadDays.ToString(), request.ApiKeyReminderLeadDays?.ToString());   // D-243
        TrackIfSent("ApiKeyExpiredRetentionDays", before.ApiKeyExpiredRetentionDays.ToString(), request.ApiKeyExpiredRetentionDays?.ToString());   // D-243
        TrackIfSent("SiteUrl", before.SiteUrl ?? "", request.SiteUrl);   // D-224
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
