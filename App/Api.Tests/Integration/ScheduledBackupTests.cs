using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using BlueTrack.Api.Backups;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.Security;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-200: a real scheduled backup of BlueTrackTest into a folder under SQL
/// Server's default backup directory (which its service account can write),
/// then the clean-up: old scheduled backups go, manual ones stay, and the run
/// is recorded in web.backup_run.
/// </summary>
public class ScheduledBackupTests
{
    [Fact]
    public async Task RunAsync_BacksUp_DeletesOnlyOldScheduledBackups_AndRecordsTheRun()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var defaultBackupPath = await connection.QuerySingleAsync<string>("SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS nvarchar(300))");
        var folder = Path.Combine(defaultBackupPath, $"IntegrationTest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        var databaseName = await connection.QuerySingleAsync<string>("SELECT DB_NAME()");
        var oldScheduled = Path.Combine(folder, $"{databaseName}_scheduled_20200101_010000.bak");
        var manual = Path.Combine(folder, $"{databaseName}_20200101_010000.bak");
        await File.WriteAllTextAsync(oldScheduled, "old");
        await File.WriteAllTextAsync(manual, "manual");
        var startedAfter = DateTime.UtcNow.AddSeconds(-1);

        var services = new ServiceCollection()
            .AddSingleton<IDbConnectionFactory>(new TestDbConnectionFactory())
            .AddSingleton(new AllowedFileRoots(new ConfigurationBuilder().Build()))
            .AddScoped<DeploymentRepository>()
            .AddScoped<BackupRunRepository>()
            .BuildServiceProvider();
        var service = new ScheduledBackupService(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System, NullLogger<ScheduledBackupService>.Instance);
        var config = new GlobalApplicationConfig
        {
            BreadcrumbPosition = "TopLeft", ExceptionIdPattern = "EXC-{yyyy}-{seq:0000}", ActiveRiskAlgorithm = "DominantPlusTail",
            DataFeedRunTime = "04:00", BusinessHoursStart = "07:00", BusinessHoursEnd = "18:00", BusinessDays = "Mon",
            SafeDecomMode = "Off", AccountDecomMode = "Off", SafeIgnoreMode = "Off",
            BackupFolder = folder, ScheduledBackupTime = "01:00", ScheduledBackupRetentionDays = 14,
        };

        try
        {
            await service.RunAsync(services, config);

            Assert.False(File.Exists(oldScheduled));
            Assert.True(File.Exists(manual));
            var written = Assert.Single(Directory.GetFiles(folder, $"{databaseName}_scheduled_*.bak"));

            var run = await connection.QuerySingleAsync<(string Outcome, string FilePath, int? FilesDeleted)>(
                "SELECT TOP 1 Outcome, FilePath, FilesDeleted FROM web.backup_run WHERE TriggerType = 'Schedule' AND StartedAt >= @startedAfter ORDER BY BackupRunKey DESC",
                new { startedAfter });
            Assert.Equal("Succeeded", run.Outcome);
            Assert.Equal(written, run.FilePath);
            Assert.Equal(1, run.FilesDeleted);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.backup_run WHERE StartedAt >= @startedAfter AND FilePath LIKE @folder + '%'", new { startedAfter, folder });
            Directory.Delete(folder, recursive: true);
        }
    }
}
