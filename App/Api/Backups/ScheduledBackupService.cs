using BlueTrack.Api.Data;
using BlueTrack.Api.DataFeeds;
using BlueTrack.Api.Security;

namespace BlueTrack.Api.Backups;

/// <summary>
/// D-200: the daily scheduled database backup. Wakes every minute, like the
/// data feeds' scheduler (DataFeedBackgroundService), and when
/// web.app_config.ScheduledBackupTime has passed since the last wake it backs
/// up the BlueTrack database into BackupFolder -- which must be inside
/// FileAccess:AllowedRoots (D-197) -- and then deletes this database's
/// scheduled backup files older than ScheduledBackupRetentionDays. Manual
/// "Backup App" files are never deleted (BackupRetention). Every run is
/// recorded in web.backup_run and shown on the Deployment page.
///
/// SQL Server writes the backup (as its service account); the API deletes
/// old ones, so the App Pool's account needs Modify on the backup folder.
/// When the folder isn't reachable from this machine (SQL Server elsewhere),
/// the backup still runs and the clean-up is reported as skipped.
/// </summary>
public sealed class ScheduledBackupService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ScheduledBackupService> logger) : BackgroundService
{
    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastWake = timeProvider.GetLocalNow().DateTime;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(WakeInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return; // Shutdown.
            }

            var now = timeProvider.GetLocalNow().DateTime;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var config = await scope.ServiceProvider.GetRequiredService<AppConfigRepository>().GetAsync();
                if (FeedSchedule.TryParseTime(config.ScheduledBackupTime, out var backupTime) && FeedSchedule.RunTimeFellBetween(lastWake, now, backupTime))
                {
                    await RunAsync(scope.ServiceProvider, config);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled backup failed unexpectedly -- will try again tomorrow.");
            }
            lastWake = now;
        }
    }

    /// <summary>One scheduled backup and its clean-up; always records a web.backup_run row.</summary>
    public async Task RunAsync(IServiceProvider services, Models.GlobalApplicationConfig config)
    {
        var runs = services.GetRequiredService<BackupRunRepository>();
        var startedAt = timeProvider.GetUtcNow().UtcDateTime;
        BackupRunRepository.BackupRun Result(string outcome, string? filePath = null, int? filesDeleted = null, string? error = null) => new()
        {
            TriggerType = "Schedule", StartedAt = startedAt, FinishedAt = timeProvider.GetUtcNow().UtcDateTime,
            Outcome = outcome, FilePath = filePath, FilesDeleted = filesDeleted, ErrorMessage = error is null ? null : Truncate(error, 2000),
        };

        if (string.IsNullOrWhiteSpace(config.BackupFolder))
        {
            await runs.RecordAsync(Result("Failed", error: "No Backup Folder is set on Global Application Configuration."));
            return;
        }
        if (services.GetRequiredService<AllowedFileRoots>().Check(config.BackupFolder) is { } folderError)
        {
            await runs.RecordAsync(Result("Failed", error: folderError));
            return;
        }

        var deployment = services.GetRequiredService<DeploymentRepository>();
        string filePath;
        try
        {
            logger.LogInformation("Starting the scheduled database backup to {Folder}.", config.BackupFolder);
            filePath = await deployment.TriggerBackupAsync(config.BackupFolder, scheduled: true);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Scheduled database backup failed.");
            await runs.RecordAsync(Result("Failed", error: ex.Message));
            return;
        }

        // Clean-up: a failure here doesn't undo a good backup, so it's reported, not "Failed".
        int? deleted = null;
        string? note = null;
        try
        {
            if (Directory.Exists(config.BackupFolder))
            {
                var databaseName = await deployment.GetDatabaseNameAsync();
                var names = Directory.EnumerateFiles(config.BackupFolder, "*.bak").Select(Path.GetFileName).OfType<string>();
                var expired = BackupRetention.FilesToDelete(names, databaseName, config.ScheduledBackupRetentionDays, timeProvider.GetUtcNow().UtcDateTime);
                foreach (var name in expired)
                {
                    File.Delete(Path.Combine(config.BackupFolder, name));
                }
                deleted = expired.Count;
            }
            else
            {
                note = "The backup succeeded; old scheduled backups weren't cleaned up because this server can't see the backup folder (is SQL Server on another machine?).";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            note = $"The backup succeeded; cleaning up old scheduled backups failed: {ex.Message} The App Pool's account needs Modify on the backup folder.";
            logger.LogWarning(ex, "Scheduled backup clean-up failed.");
        }

        logger.LogInformation("Scheduled database backup written to {File}; {Deleted} old scheduled backups deleted.", filePath, deleted);
        await runs.RecordAsync(Result("Succeeded", filePath, deleted, note));
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];
}
