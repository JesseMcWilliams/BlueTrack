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
/// D-200: a real scheduled backup of BlueTrackTest into a folder the test
/// creates and grants SQL Server's service account access to, then the
/// clean-up: old scheduled backups go, manual ones stay, and the run
/// is recorded in web.backup_run.
/// </summary>
public class ScheduledBackupTests
{
    [Fact]
    public async Task RunAsync_BacksUp_DeletesOnlyOldScheduledBackups_AndRecordsTheRun()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        // A folder this test owns (under its own output directory, which the CI
        // runner's non-admin account can write), opened up to SQL Server's
        // service account, which writes the backup file.
        var folder = Path.Combine(AppContext.BaseDirectory, $"IntegrationTest_backup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        string? serviceAccount = null;
        try
        {
            serviceAccount = await connection.QuerySingleOrDefaultAsync<string>(
                "SELECT TOP 1 service_account FROM sys.dm_server_services WHERE filename LIKE '%sqlservr.exe%'");
        }
        catch (SqlException)
        {
            // Needs VIEW SERVER STATE; fall back to the default instance's service account.
        }
        serviceAccount ??= @"NT SERVICE\MSSQLSERVER";
        using (var icacls = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("icacls", [folder, "/grant", $"{serviceAccount}:(OI)(CI)M"])
               { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!)
        {
            await icacls.WaitForExitAsync();
            Assert.True(icacls.ExitCode == 0, $"Could not grant {serviceAccount} access to the test folder: {await icacls.StandardError.ReadToEndAsync()}");
        }
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
