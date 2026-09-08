using System.IO.Compression;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// Backs the new Deployment admin page (Design_Admin_Deployment_Management.md,
/// D-96, Part 3) -- read-only environment/version info, health checks, and
/// SQL Server backup status.
///
/// HealthCheckService is called directly here rather than mapping a
/// separate /health route: this app doesn't expose anything unauthenticated
/// elsewhere, and the results are meant for an admin, not an infra probe --
/// AddHealthChecks() in Program.cs still registers real IHealthCheck
/// implementations against ASP.NET Core's own health checks middleware, just
/// consumed through this permission-gated endpoint instead of an anonymous one.
/// </summary>
[ApiController]
[Route("api/admin/deployment")]
[Authorize(Policy = Permissions.ViewDeploymentInfo)]
public sealed class DeploymentController(
    IHostEnvironment hostEnvironment,
    HealthCheckService healthCheckService,
    DeploymentRepository repository,
    AppConfigRepository appConfigRepository) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var healthReport = await healthCheckService.CheckHealthAsync();
        var backupStatus = await repository.GetBackupStatusAsync();

        var assembly = Assembly.GetExecutingAssembly();
        var version = assembly.GetName().Version?.ToString() ?? "unknown";
        var buildTimestampUtc = string.IsNullOrEmpty(assembly.Location)
            ? (DateTime?)null
            : System.IO.File.GetLastWriteTimeUtc(assembly.Location);

        return Ok(new DeploymentInfoResponse
        {
            EnvironmentName = hostEnvironment.EnvironmentName,
            Version = version,
            BuildTimestampUtc = buildTimestampUtc,
            HealthChecks = healthReport.Entries.Select(entry => new HealthCheckEntryResponse
            {
                Name = entry.Key,
                Status = entry.Value.Status.ToString(),
                Description = entry.Value.Description
            }).ToList(),
            BackupStatus = backupStatus
        });
    }

    /// <summary>
    /// D-117: "Backup App" button. Triggers a real BACKUP DATABASE (to
    /// web.app_config.BackupFolder, an admin-set path -- not SQL Server's
    /// own default backup directory, so this app's own admin UI decides
    /// where its backups land) and returns a downloadable zip of
    /// appsettings*.json alongside it. Nothing DPAPI-related is zipped:
    /// WindowsDpapiProtector creates no key file of its own (Windows manages
    /// DPAPI's machine/user master keys transparently) -- the only DPAPI
    /// material that exists at all is ciphertext inside web.credential/
    /// web.notification_config/etc., which the BACKUP DATABASE step above
    /// necessarily includes (there's no column-level exclusion in T-SQL
    /// BACKUP), consistent with D-65's existing disaster-recovery note that
    /// DPAPI ciphertext only ever decrypts on this one machine anyway.
    ///
    /// Requires TriggerBackup on top of the class-level ViewDeploymentInfo --
    /// ASP.NET Core combines multiple [Authorize] policies with AND, so this
    /// action needs both (Admin holds both by default, per the schema's own
    /// explicit grant).
    /// </summary>
    [HttpPost("backup")]
    [Authorize(Policy = Permissions.TriggerBackup)]
    public async Task<IActionResult> Backup()
    {
        var config = await appConfigRepository.GetAsync();
        if (string.IsNullOrWhiteSpace(config.BackupFolder))
        {
            return Problem(title: "Backup folder not configured",
                detail: "Set a Backup Folder on the Global Application Configuration admin page first.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        string backupFilePath;
        try
        {
            backupFilePath = await repository.TriggerBackupAsync(config.BackupFolder);
        }
        catch (Exception ex)
        {
            return Problem(title: "Database backup failed", detail: ex.Message, statusCode: StatusCodes.Status500InternalServerError);
        }

        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var configFileName in new[] { "appsettings.json", "appsettings.Production.json" })
            {
                var sourcePath = Path.Combine(AppContext.BaseDirectory, configFileName);
                if (System.IO.File.Exists(sourcePath))
                {
                    archive.CreateEntryFromFile(sourcePath, configFileName);
                }
            }

            var noteEntry = archive.CreateEntry("BACKUP_NOTE.txt");
            await using var noteStream = new StreamWriter(noteEntry.Open());
            await noteStream.WriteAsync(
                $"Database backed up to: {backupFilePath}\nTaken: {DateTime.UtcNow:O} UTC\n");
        }

        zipStream.Position = 0;
        return File(zipStream.ToArray(), "application/zip", $"BlueTrack_ConfigBackup_{DateTime.UtcNow:yyyyMMdd_HHmmss}.zip");
    }
}
