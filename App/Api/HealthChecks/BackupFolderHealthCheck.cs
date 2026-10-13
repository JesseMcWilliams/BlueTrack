using System.Security.AccessControl;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using BlueTrack.Api.Backups;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.HealthChecks;

/// <summary>
/// D-203: reports, without changing anything, whether broad groups can read
/// the Backup Folder. The next backup fixes it if it can, or refuses
/// (BackupFolderAccess); this shows the problem before that.
/// </summary>
public sealed class BackupFolderHealthCheck(AppConfigRepository appConfigRepository) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var folder = (await appConfigRepository.GetAsync()).BackupFolder;
        if (string.IsNullOrWhiteSpace(folder)) return HealthCheckResult.Healthy("No Backup Folder is set.");

        var directory = new DirectoryInfo(folder);
        if (!directory.Exists) return HealthCheckResult.Healthy("This server can't see the Backup Folder, so its permissions can't be checked here.");

        DirectorySecurity security;
        try
        {
            security = directory.GetAccessControl();
        }
        catch (UnauthorizedAccessException)
        {
            return HealthCheckResult.Degraded("The App Pool's account can't read the Backup Folder's permissions.");
        }

        var readers = BackupFolderAccess.BroadReaders(security);
        return readers.Count == 0
            ? HealthCheckResult.Healthy("Only specific accounts can read the Backup Folder.")
            : HealthCheckResult.Degraded($"{string.Join(", ", readers)} can read the Backup Folder. The next backup removes that access, or is refused if it can't.");
    }
}
