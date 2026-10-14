using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.DataFeeds;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-211 (Data Sources phase 3, Design_Data-Sources.md): the CyberArk
/// sources on Admin > Data Sources. The settings are read each night by
/// dbo.usp_Import_Configured, the Agent job's Import step; there is no Run
/// now for these sources (decided 2026-10-09).
/// </summary>
[ApiController]
[Route("api/admin/cyberark-sources")]
[Authorize(Policy = Permissions.ManageDataSources)]
public sealed class CyberArkSourcesController(
    CyberArkSourceConfigRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger,
    TimeProvider timeProvider,
    Security.AllowedFileRoots allowedFileRoots) : ControllerBase
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
        if (settings.ImportSelfHosted)
        {
            var exists = await repository.DatabaseExistsAsync(settings.EvdDatabaseName!);
            checks.Add(new CyberArkSourceTestCheck
            {
                Item = "EVD database",
                Ok = exists,
                Message = exists ? $"{settings.EvdDatabaseName} exists." : $"No database named {settings.EvdDatabaseName} on BlueTrack's SQL Server."
            });
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
