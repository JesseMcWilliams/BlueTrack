using System.Net;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-337: GET /api/admin/etl-runs (Admin > Data Sources, Import and Load) and a run's steps.</summary>
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
        var response = await Client("TestUser.Viewer").GetAsync("/api/admin/etl-runs");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
