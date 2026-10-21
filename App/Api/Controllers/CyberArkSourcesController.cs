using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.CyberArkDirect;
using BlueTrack.Api.Data;
using BlueTrack.Api.DataFeeds;
using BlueTrack.Api.Models;
using BlueTrack.Api.Secrets;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-211 (Data Sources phase 3, Design_Data-Sources.md): the CyberArk
/// sources on Admin > Data Sources. The settings are read each night by
/// dbo.usp_Import_Configured, the Agent job's Import step; there is no Run
/// now for the EVD or the Privilege Cloud folder (decided 2026-10-09).
/// D-248: Self-Hosted can instead be pulled by the API from PVWA's REST API
/// (Self-Hosted Direct); that source has a Test, a pull log and a Run now,
/// because a pull rejected by the change guard needs a way to be accepted.
/// D-271: with the EVD as the source, the shadow pull uses the same Test, log and Run now, and the comparison of
/// its data with the EVD's has its own endpoints (direct/comparison).
/// </summary>
[ApiController]
[Route("api/admin/cyberark-sources")]
[Authorize(Policy = Permissions.ManageDataSources)]
public sealed class CyberArkSourcesController(
    CyberArkSourceConfigRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger,
    TimeProvider timeProvider,
    Security.AllowedFileRoots allowedFileRoots,
    SelfHostedDirectRepository directRepository,
    SelfHostedDirectRunner directRunner,
    SelfHostedDirectPullService directPullService,
    CredentialRepository credentialRepository,
    SecretsStoreRepository secretsStoreRepository,
    ShadowComparisonRepository comparisonRepository) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get() => Ok(await repository.GetAsync());

    [HttpPut]
    public async Task<IActionResult> Save([FromBody] CyberArkSourceSettings request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var settings = CyberArkSourceValidation.Normalize(request);
        var errors = CyberArkSourceValidation.Validate(settings, allowedFileRoots);
        if (errors.Count > 0)
        {
            return Problem(title: "Invalid CyberArk source settings", detail: string.Join(" ", errors), statusCode: StatusCodes.Status400BadRequest);
        }

        // D-270: the key is kept with the EVD selected too (for the switch back), so it is checked whenever it is set.
        if (settings.PvwaCredentialKey is { } credentialKey && !await repository.CredentialExistsAsync(credentialKey))
        {
            return Problem(title: "Invalid CyberArk source settings", detail: "The credential doesn't exist. Choose one from Admin > Credentials.", statusCode: StatusCodes.Status400BadRequest);
        }

        var before = await repository.GetAsync();
        await repository.SaveAsync(settings, user.UserKey);

        List<FieldChange> changes = [];
        void Track(string field, string? beforeValue, string? newValue)
        {
            if (!before.IsConfigured || beforeValue != newValue) changes.Add(new FieldChange(field, before.IsConfigured ? beforeValue : null, newValue));
        }
        var old = before.Settings;
        Track(nameof(settings.ImportPrivilegeCloud), old.ImportPrivilegeCloud.ToString(), settings.ImportPrivilegeCloud.ToString());
        Track(nameof(settings.ExportFolderPath), old.ExportFolderPath, settings.ExportFolderPath);
        foreach (var ((field, _, newName), (_, _, oldName)) in settings.Files().Zip(old.Files()))
        {
            Track(field, oldName, newName);
        }
        Track(nameof(settings.ImportSelfHosted), old.ImportSelfHosted.ToString(), settings.ImportSelfHosted.ToString());
        Track(nameof(settings.EvdDatabaseName), old.EvdDatabaseName, settings.EvdDatabaseName);
        Track(nameof(settings.SelfHostedSource), old.SelfHostedSource, settings.SelfHostedSource);
        Track(nameof(settings.PvwaBaseUrl), old.PvwaBaseUrl, settings.PvwaBaseUrl);
        Track(nameof(settings.PvwaCredentialKey), old.PvwaCredentialKey?.ToString(), settings.PvwaCredentialKey?.ToString());
        Track(nameof(settings.PvwaLogonMethod), old.PvwaLogonMethod, settings.PvwaLogonMethod);
        Track(nameof(settings.DirectPullTime), old.DirectPullTime, settings.DirectPullTime);
        Track(nameof(settings.DirectMaxChangePercent), old.DirectMaxChangePercent.ToString(), settings.DirectMaxChangePercent.ToString());
        Track(nameof(settings.DirectPullTimeLimitMinutes), old.DirectPullTimeLimitMinutes.ToString(), settings.DirectPullTimeLimitMinutes.ToString());
        Track(nameof(settings.ShadowPullEnabled), old.ShadowPullEnabled.ToString(), settings.ShadowPullEnabled.ToString());
        Track(nameof(settings.ShadowPullTime), old.ShadowPullTime, settings.ShadowPullTime);
        if (changes.Count > 0)
        {
            await auditLogger.LogAsync("FieldEdit", user.UserKey, "cyberark_source_config", "1",
                detail: before.IsConfigured ? "CyberArk source settings changed" : "CyberArk source settings created", fieldChanges: changes);
        }
        return NoContent();
    }

    /// <summary>
    /// Test, on unsaved values: whether the EVD database exists, and whether
    /// the export folder and today's files exist as the app pool sees them,
    /// with the columns the import needs in each file's header (D-228).
    /// SQL Server's service account reads the files at import, so it needs
    /// read access as well; the page says so.
    /// </summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] CyberArkSourceSettings request)
    {
        var settings = CyberArkSourceValidation.Normalize(request);
        var errors = CyberArkSourceValidation.Validate(settings, allowedFileRoots);
        if (errors.Count > 0) return BadRequest(new { error = string.Join(" ", errors) });

        var checks = new List<CyberArkSourceTestCheck>();
        if (settings.ImportSelfHosted && settings.SelfHostedSource == "Direct")
        {
            checks.AddRange(await directPullService.TestAsync(settings, HttpContext.RequestAborted));
        }
        else if (settings.ImportSelfHosted)
        {
            var exists = await repository.DatabaseExistsAsync(settings.EvdDatabaseName!);
            checks.Add(new CyberArkSourceTestCheck
            {
                Item = "EVD database",
                Ok = exists,
                Message = exists ? $"{settings.EvdDatabaseName} exists." : $"No database named {settings.EvdDatabaseName} on BlueTrack's SQL Server."
            });
            // D-271: the shadow pull reads PVWA with the same settings Direct would.
            if (settings.ShadowPullEnabled) checks.AddRange(await directPullService.TestAsync(settings, HttpContext.RequestAborted));
        }

        if (settings.ImportPrivilegeCloud)
        {
            var folder = settings.ExportFolderPath!;
            var folderFound = Directory.Exists(folder);
            checks.Add(new CyberArkSourceTestCheck
            {
                Item = "Export folder",
                Ok = folderFound,
                Message = folderFound ? $"{folder} is readable." : "Folder not found, or the app pool can't read it."
            });
            if (folderFound)
            {
                var today = timeProvider.GetLocalNow().DateTime;
                var columns = await repository.GetImportColumnsAsync();
                foreach (var (field, label, fileName) in settings.Files())
                {
                    var expanded = FeedFileResolver.ExpandPattern(fileName, today);
                    var path = Path.Combine(folder, expanded);
                    if (!System.IO.File.Exists(path))
                    {
                        checks.Add(new CyberArkSourceTestCheck { Item = label, Ok = false, Message = $"{expanded} not found today." });
                        continue;
                    }
                    var kind = CyberArkImportColumns.FileKindOf(field);
                    checks.Add(await CheckHeaderAsync(label, expanded, path, columns.Where(c => c.FileKind == kind)));
                }
            }
        }
        return Ok(checks);
    }

    /// <summary>
    /// D-248: the named credentials the Direct source can use, names only. Admin > Credentials has its own list, but
    /// it needs a different permission than this page does, and a secret never leaves the server either way.
    /// </summary>
    [HttpGet("credentials")]
    public async Task<IActionResult> Credentials()
    {
        var result = new List<object>();
        foreach (var c in await credentialRepository.GetAllAsync())
        {
            // D-250: whether the backend this credential uses is set up (CP needs an AppID), so the page can say so.
            var (ready, note) = await CredentialBackendCheck.CheckAsync(secretsStoreRepository, c.BackendType);
            result.Add(new { c.CredentialKey, c.CredentialName, c.BackendType, BackendReady = ready, BackendNote = note });
        }
        return Ok(result);
    }

    /// <summary>
    /// D-250: just enough for a warning marker on the admin menu: whether Self-Hosted Direct is the saved source and how
    /// the latest pull ended. Cheap, so the menu can ask on every admin page.
    /// </summary>
    [HttpGet("direct/status")]
    public async Task<IActionResult> DirectStatus()
    {
        var settings = (await repository.GetAsync()).Settings;
        if (!settings.ImportSelfHosted || settings.SelfHostedSource != "Direct") return Ok(new { enabled = false });
        var latest = (await directRepository.GetRecentAsync(1)).FirstOrDefault();
        return Ok(new { enabled = true, latestStatus = latest?.Status, latestMessage = latest?.Message });
    }

    /// <summary>D-248: the latest Self-Hosted Direct pulls, newest first, and whether one is running now.</summary>
    [HttpGet("direct/pulls")]
    public async Task<IActionResult> DirectPulls() =>
        Ok(new { running = directRunner.IsRunning, pulls = await directRepository.GetRecentAsync(15) });

    /// <summary>
    /// D-248: Run now for Self-Hosted Direct: starts a pull in the background (the page polls the log). With
    /// <c>acceptChange</c> a change over the allowed percentage is accepted instead of rejected. Audit-logged.
    /// </summary>
    [HttpPost("direct/pull")]
    public async Task<IActionResult> DirectPull([FromBody] DirectPullRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var settings = (await repository.GetAsync()).Settings;
        var mode = SelfHostedDirectPullService.ModeFor(settings);
        if (mode is null)
        {
            return Problem(title: "Self-Hosted Direct isn't the saved source", detail: "Save Self-Hosted Direct as the source, or turn on the shadow pull, first.", statusCode: StatusCodes.Status400BadRequest);
        }
        if (!directRunner.TryStart(user.DisplayName ?? "an admin", request.AcceptChange))
        {
            return Problem(title: "A pull is already running", statusCode: StatusCodes.Status409Conflict);
        }
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "cyberark_source_config", "1",
            detail: mode == "Shadow" ? "Self-Hosted Direct shadow pull started"
                : request.AcceptChange ? "Self-Hosted Direct pull started, accepting a change over the allowed percentage" : "Self-Hosted Direct pull started");
        return Accepted();
    }

    /// <summary>
    /// D-271: the shadow comparison: the clean nights so far (and how many are needed), the recent runs, the latest
    /// run's differences (blocking first, at most 500, optionally one category or only blocking ones), the Safes PVWA
    /// can't fully see, and the marks.
    /// </summary>
    [HttpGet("direct/comparison")]
    public async Task<IActionResult> Comparison([FromQuery] string? category, [FromQuery] bool blockingOnly = false)
    {
        if (category is not null && !ShadowComparisonRepository.DifferenceCategories.Contains(category))
            return Problem(title: "Unknown difference category", statusCode: StatusCodes.Status400BadRequest);

        var runs = await comparisonRepository.GetRunsAsync(60);
        var (differences, total) = await comparisonRepository.GetDifferencesAsync(category, blockingOnly, 500);
        return Ok(new
        {
            cleanNightsNeeded = ShadowCleanNights.Needed,
            cleanNights = ShadowCleanNights.Count(runs, timeProvider.GetLocalNow().DateTime, timeProvider.LocalTimeZone),
            runs = runs.Take(30),
            differences,
            totalDifferences = total,
            safes = await comparisonRepository.GetSafeVisibilityAsync(),
            marks = await comparisonRepository.GetMarksAsync()
        });
    }

    /// <summary>
    /// D-271: marks the item behind a difference as expected, so it no longer stops a clean night. <c>Scope</c> picks
    /// what is marked: the item itself, its whole Safe, or (for a Safe member) every membership of that user or group.
    /// Audit-logged; the latest comparison is re-marked at once.
    /// </summary>
    [HttpPost("direct/comparison/marks")]
    public async Task<IActionResult> AddMark([FromBody] ShadowMarkRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        if (!ShadowComparisonRepository.MarkCategories.Contains(request.Category))
            return Problem(title: "Invalid mark", detail: "Category must be Internal or Expected.", statusCode: StatusCodes.Status400BadRequest);
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > 500 })
            return Problem(title: "Invalid mark", detail: "The note must be 500 characters or fewer.", statusCode: StatusCodes.Status400BadRequest);

        var difference = await comparisonRepository.GetDifferenceAsync(request.DifferenceKey);
        if (difference is null) return Problem(title: "That difference is no longer in the latest comparison", statusCode: StatusCodes.Status404NotFound);

        (string Kind, string Key, string? Name)? item = request.Scope switch
        {
            "Item" => (difference.ItemKind, difference.ItemKey, difference.ItemName),
            "Safe" when difference.SafeNumber is { } safe => ("Safe", safe.ToString(System.Globalization.CultureInfo.InvariantCulture), difference.SafeName),
            "Principal" when difference.ItemKind == "Member" && difference.PrincipalId is { } id =>
                (await comparisonRepository.IsGroupAsync(id) ? "Group" : "User", id.ToString(System.Globalization.CultureInfo.InvariantCulture), difference.ItemName),
            _ => null
        };
        if (item is not { } it)
            return Problem(title: "Invalid mark", detail: "Scope must be Item, Safe (for a difference in a Safe) or Principal (for a Safe member).", statusCode: StatusCodes.Status400BadRequest);

        var markKey = await comparisonRepository.SaveMarkAsync(it.Kind, it.Key, it.Name, request.Category, note, user.UserKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "shd_comparison_mark", markKey.ToString(System.Globalization.CultureInfo.InvariantCulture),
            detail: $"Shadow comparison: marked {it.Kind} '{it.Name ?? it.Key}' as {request.Category}" + (note is null ? "" : $" ({note})"));
        return NoContent();
    }

    /// <summary>D-271: removes a mark; the difference counts against a clean night again. Audit-logged.</summary>
    [HttpDelete("direct/comparison/marks/{markKey:int}")]
    public async Task<IActionResult> DeleteMark(int markKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        var mark = await comparisonRepository.DeleteMarkAsync(markKey);
        if (mark is null) return NotFound();
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "shd_comparison_mark", markKey.ToString(System.Globalization.CultureInfo.InvariantCulture),
            detail: $"Shadow comparison: removed the {mark.Category} mark on {mark.ItemKind} '{mark.ItemName ?? mark.ItemKey}'");
        return NoContent();
    }

    /// <summary>
    /// D-228: reads only the file's header row and reports the required
    /// columns it lacks (the import would fail), or OK, noting optional
    /// columns it lacks (they import as empty).
    /// </summary>
    private static async Task<CyberArkSourceTestCheck> CheckHeaderAsync(
        string label, string fileName, string path, IEnumerable<CyberArkImportColumn> columnsOfKind)
    {
        IReadOnlyList<string> header;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            header = await RiskScoring.CsvFileReader.ReadHeaderAsync(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CsvHelper.CsvHelperException)
        {
            return new CyberArkSourceTestCheck { Item = label, Ok = false, Message = $"{fileName} found, but it could not be read: {ex.Message}" };
        }

        var (missingRequired, missingOptional) = CyberArkImportColumns.Compare(header, columnsOfKind);
        if (missingRequired.Count > 0)
        {
            return new CyberArkSourceTestCheck
            {
                Item = label,
                Ok = false,
                Message = $"{fileName} is missing the required column(s): {string.Join(", ", missingRequired)}. The import would fail."
            };
        }
        var note = missingOptional.Count == 0 ? "" : $" Not in the file, so imported as empty: {string.Join(", ", missingOptional)}.";
        return new CyberArkSourceTestCheck { Item = label, Ok = true, Message = $"{fileName} found, with the required columns.{note}" };
    }
}

/// <summary>D-271: body of marking a shadow comparison difference as expected.</summary>
public sealed class ShadowMarkRequest
{
    public long DifferenceKey { get; init; }
    /// <summary>Item (the difference's own Safe, account, user, group or membership), Safe, or Principal (a member's user or group).</summary>
    public string Scope { get; init; } = "Item";
    /// <summary>Internal (a CyberArk internal object REST doesn't return) or Expected.</summary>
    public string Category { get; init; } = "Expected";
    public string? Note { get; init; }
}

/// <summary>D-248: body of Run now for Self-Hosted Direct.</summary>
public sealed class DirectPullRequest
{
    /// <summary>Accept a Safe or account count change larger than the allowed percentage (an admin has checked it).</summary>
    public bool AcceptChange { get; init; }
}
