using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-337: GET /api/admin/etl-runs (Admin > Data Sources, Import and Load) and a run's steps. D-348: the stall time and Cancel load.</summary>
public class EtlRunsControllerTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public EtlRunsControllerTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    [Fact]
    public async Task ListsTheLatestRuns_TheirSteps_AndTheOnesStillRunning()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var runKey = await connection.QuerySingleAsync<long>("""
            INSERT INTO dbo.etl_run_log (StepName, SessionId) VALUES ('Load', 32001);
            DECLARE @key BIGINT = SCOPE_IDENTITY();
            INSERT INTO dbo.etl_run_step (EtlRunKey, StepOrder, ProcName, StartedAt, CompletedAt, Status) VALUES
                (@key, 1, N'dbo.usp_Load_DimLocation', SYSUTCDATETIME(), SYSUTCDATETIME(), N'Succeeded'),
                (@key, 2, N'dbo.usp_Load_DimPlatform', SYSUTCDATETIME(), NULL, N'Started');
            SELECT @key;
            """);
        try
        {
            var client = Client("TestUser.Admin");
            var body = JsonNode.Parse(await client.GetStringAsync("/api/admin/etl-runs"))!;
            Assert.NotNull(body["serverTime"]);
            var run = body["runs"]!.AsArray().Single(r => (long)r!["etlRunKey"]! == runKey)!;
            Assert.Equal(("Load", "Started", 2), ((string)run["stepName"]!, (string)run["status"]!, (int)run["stepCount"]!));

            var running = body["running"]!.AsArray().Single(r => (long)r!["activity"]!["etlRunKey"]! == runKey)!;
            Assert.Equal(("dbo.usp_Load_DimPlatform", 1), ((string)running["currentStep"]!["procName"]!, (int)running["stepsDone"]!));
            Assert.False((bool)running["activity"]!["sessionFound"]!);   // no such session: the run will never finish

            var steps = JsonNode.Parse(await client.GetStringAsync($"/api/admin/etl-runs/{runKey}/steps"))!.AsArray();
            Assert.Equal(["Succeeded", "Started"], steps.Select(s => (string)s!["status"]!));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM dbo.etl_run_log WHERE EtlRunKey = @runKey", new { runKey });
        }
    }

    [Fact]
    public async Task NeedsManageDataSources()
    {
        var viewer = Client("TestUser.Viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/admin/etl-runs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PutAsJsonAsync("/api/admin/etl-runs/settings", new { stallMinutes = 45 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync("/api/admin/etl-runs/cancel", null)).StatusCode);
    }

    [Fact]
    public async Task TheStallTime_IsValidated_SavedAndAudited()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var original = await connection.QuerySingleAsync<int>("SELECT TOP (1) RunStallMinutes FROM web.app_config");
        var since = await connection.QuerySingleAsync<DateTime>("SELECT SYSUTCDATETIME()");
        try
        {
            var admin = Client("TestUser.Admin");
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/etl-runs/settings", new { stallMinutes = 4 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/etl-runs/settings", new { stallMinutes = 1441 })).StatusCode);
            (await admin.PutAsJsonAsync("/api/admin/etl-runs/settings", new { stallMinutes = 45 })).EnsureSuccessStatusCode();

            var body = JsonNode.Parse(await admin.GetStringAsync("/api/admin/etl-runs"))!;
            Assert.Equal(45, (int)body["stallMinutes"]!);
            var detail = await connection.QuerySingleAsync<string>("""
                SELECT TOP (1) e.Detail FROM web.audit_event e JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey
                WHERE t.EventTypeName = 'ImportLoadSettingsChanged' AND e.OccurredAt >= @since ORDER BY e.AuditEventKey DESC
                """, new { since });
            Assert.EndsWith("-> 45 minutes.", detail);
        }
        finally
        {
            await connection.ExecuteAsync("UPDATE web.app_config SET RunStallMinutes = @original", new { original });
        }
    }

    [Fact]
    public async Task CancelLoad_Returns409_WithTheAgentsReason_WhenTheJobCantBeStopped()
    {
        // The test databases have no Import and Load job, so SQL Server Agent refuses.
        var response = await Client("TestUser.Admin").PostAsync("/api/admin/etl-runs/cancel", null);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Contains("Import and Load", (string)problem["detail"]!);
    }

    [Fact]
    public async Task AnImport_ListsTheFilesItImported_AndALoadHasNone()
    {
        var token = $"etlfiles{Guid.NewGuid():N}"[..16];
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var (importKey, loadKey) = await connection.QuerySingleAsync<(long, long)>("""
            INSERT INTO dbo.etl_run_log (StepName, StartedAt, CompletedAt, Status) VALUES ('Import', DATEADD(MINUTE, -2, SYSUTCDATETIME()), SYSUTCDATETIME(), 'Failed');
            DECLARE @i BIGINT = SCOPE_IDENTITY();
            INSERT INTO dbo.etl_run_log (StepName, StartedAt, CompletedAt, Status) VALUES ('Load', DATEADD(MINUTE, -2, SYSUTCDATETIME()), SYSUTCDATETIME(), 'Succeeded');
            DECLARE @l BIGINT = SCOPE_IDENTITY();
            DECLARE @pc INT = (SELECT TOP (1) SourceSystemKey FROM dbo.dim_source_system ORDER BY SourceSystemKey);
            INSERT INTO dbo.import_log (SourceSystemKey, SourceFileName, RowsLoaded, StartedAt, CompletedAt, Status, ErrorMessage) VALUES
                (@pc, @token + '_Safes.csv', 12, DATEADD(SECOND, -100, SYSUTCDATETIME()), DATEADD(SECOND, -99, SYSUTCDATETIME()), 'Succeeded', NULL),
                (@pc, @token + '_Accounts.csv', NULL, DATEADD(SECOND, -90, SYSUTCDATETIME()), DATEADD(SECOND, -89, SYSUTCDATETIME()), 'Failed', 'Cannot bulk load');
            SELECT @i, @l;
            """, new { token });
        try
        {
            var client = Client("TestUser.Admin");
            var detail = JsonNode.Parse(await client.GetStringAsync($"/api/admin/etl-runs/{importKey}/import"))!;
            var mine = detail["files"]!.AsArray().Where(f => ((string)f!["sourceFileName"]!).StartsWith(token, StringComparison.Ordinal)).ToList();
            Assert.Equal([$"{token}_Safes.csv", $"{token}_Accounts.csv"], mine.Select(f => (string)f!["sourceFileName"]!));
            Assert.Equal(("Failed", "Cannot bulk load"), ((string)mine[1]!["status"]!, (string)mine[1]!["errorMessage"]!));

            var runs = JsonNode.Parse(await client.GetStringAsync("/api/admin/etl-runs"))!["runs"]!.AsArray();
            Assert.True((int)runs.Single(r => (long)r!["etlRunKey"]! == importKey)!["fileCount"]! >= 2);

            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/admin/etl-runs/{loadKey}/import")).StatusCode);
        }
        finally
        {
            await connection.ExecuteAsync("""
                DELETE FROM dbo.import_log WHERE SourceFileName LIKE @token + '%';
                DELETE FROM dbo.etl_run_log WHERE EtlRunKey IN (@importKey, @loadKey);
                """, new { token, importKey, loadKey });
        }
    }
}
