using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-117: the Deployment page's "Backup App" button. Restores
/// web.app_config.BackupFolder to its original value afterward, matching
/// AdminControllersFunctionalTests's singleton-row convention. This test
/// does trigger a real BACKUP DATABASE against BlueTrackTest -- consistent
/// with this project's "real infrastructure, not mocks" testing philosophy
/// (Design_Testing_Strategy.md) -- writing a real (small, disposable-database)
/// .bak file to SQL Server's own default backup directory on every run.
/// </summary>
public class DeploymentBackupTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public DeploymentBackupTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");
        return client;
    }

    [Fact]
    public async Task Backup_NoBackupFolderConfigured_ReturnsBadRequest()
    {
        var client = AdminClient();
        var original = await client.GetFromJsonAsync<ConfigResponse>("/api/admin/configuration");

        try
        {
            await SetBackupFolderAsync(client, original!, null);

            var response = await client.PostAsync("/api/admin/deployment/backup", null);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            await SetBackupFolderAsync(client, original!, original!.BackupFolder);
        }
    }

    [Fact]
    public async Task Backup_RealBackupFolder_TriggersRealBackup_ReturnsDownloadableZip()
    {
        // SQL Server's own default backup directory -- guaranteed writable
        // by the SQL Server service account, sidestepping a separate
        // permissions question this test doesn't need to answer (confirmed
        // live against the real environment during D-117's own verification).
        var client = AdminClient();
        var original = await client.GetFromJsonAsync<ConfigResponse>("/api/admin/configuration");
        var backupFolder = await GetDefaultBackupPathAsync();

        try
        {
            await SetBackupFolderAsync(client, original!, backupFolder);

            var response = await client.PostAsync("/api/admin/deployment/backup", null);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.True(bytes.Length > 0);

            // A real zip file starts with the local file header signature "PK\x03\x04".
            Assert.Equal((byte)'P', bytes[0]);
            Assert.Equal((byte)'K', bytes[1]);
        }
        finally
        {
            await SetBackupFolderAsync(client, original!, original!.BackupFolder);
        }
    }

    private static async Task<string> GetDefaultBackupPathAsync()
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(BlueTrack.Api.Tests.Integration.TestDatabase.ConnectionString);
        return await Dapper.SqlMapper.QuerySingleAsync<string>(connection, "SELECT CAST(SERVERPROPERTY('InstanceDefaultBackupPath') AS NVARCHAR(500))");
    }

    private static Task SetBackupFolderAsync(HttpClient client, ConfigResponse based, string? backupFolder) =>
        client.PutAsJsonAsync("/api/admin/configuration", new
        {
            idleTimeoutMinutes = based.IdleTimeoutMinutes,
            breadcrumbPosition = based.BreadcrumbPosition,
            exceptionIdPattern = based.ExceptionIdPattern,
            lockTimeoutMinutes = based.LockTimeoutMinutes,
            retentionDays = based.RetentionDays,
            logReadEvents = based.LogReadEvents,
            backupFolder
        });

    private sealed class ConfigResponse
    {
        public int IdleTimeoutMinutes { get; set; }
        public string BreadcrumbPosition { get; set; } = "";
        public string ExceptionIdPattern { get; set; } = "";
        public int LockTimeoutMinutes { get; set; }
        public int? RetentionDays { get; set; }
        public bool LogReadEvents { get; set; }
        public string? BackupFolder { get; set; }
    }
}
