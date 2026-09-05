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
    DeploymentRepository repository) : ControllerBase
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
}
