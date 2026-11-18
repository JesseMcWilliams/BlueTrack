using System.Net.Http.Json;
using Dapper;
using BlueTrack.Api.Data;
using BlueTrack.Api.Tests.Integration;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-361: the API records its own start in web.api_run, and the Deployment page lists the latest starts.</summary>
public class DeploymentApiRunsTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public DeploymentApiRunsTests(BlueTrackWebApplicationFactory factory) => _factory = factory;

    private sealed record ApiRunRow(long ApiRunKey, DateTime StartedAt, DateTime? StoppedAt, string MachineName, int ProcessId, string Version,
        DateTime? PreviousStoppedAt, bool PreviousStoppedCleanly);

    [Fact]
    public async Task ApiRuns_ListsThisProcessStart_NotYetStopped()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");

        var runs = await client.GetFromJsonAsync<List<ApiRunRow>>("/api/admin/deployment/api-runs");

        Assert.NotNull(runs);
        Assert.InRange(runs!.Count, 1, 20);
        // Every test host in this run is this process, so there may be several; each records its own start.
        var mine = runs.Where(r => r.ProcessId == Environment.ProcessId && r.StoppedAt is null && r.StartedAt > DateTime.UtcNow.AddHours(-1)).ToList();
        Assert.NotEmpty(mine);
        Assert.All(mine, r => Assert.Equal(Environment.MachineName, r.MachineName));
        Assert.All(mine, r => Assert.False(string.IsNullOrEmpty(r.Version)));
        Assert.Equal(runs.OrderByDescending(r => r.ApiRunKey).Select(r => r.ApiRunKey), runs.Select(r => r.ApiRunKey));
    }

    [Fact]
    public async Task Repository_GapBeforeAStart_IsThePreviousStop_OrItsStartWhenItNeverStopped()
    {
        // Three runs with keys next to each other (the table lock keeps other test hosts' starts out): one that never
        // stopped, one that stopped cleanly, and the newest.
        long first;
        using (var connection = new SqlConnection(TestDatabase.ConnectionString))
        {
            first = await connection.ExecuteScalarAsync<long>("""
                SET XACT_ABORT ON;
                BEGIN TRAN;
                INSERT INTO web.api_run WITH (TABLOCKX) (StartedAt, StoppedAt, MachineName, ProcessId, Version)
                VALUES (DATEADD(HOUR, -3, SYSUTCDATETIME()), NULL, N'TEST-GAP', 1, N'test');
                DECLARE @first BIGINT = SCOPE_IDENTITY();
                INSERT INTO web.api_run (StartedAt, StoppedAt, MachineName, ProcessId, Version)
                VALUES (DATEADD(HOUR, -2, SYSUTCDATETIME()), DATEADD(MINUTE, -90, SYSUTCDATETIME()), N'TEST-GAP', 2, N'test'),
                       (DATEADD(MINUTE, -30, SYSUTCDATETIME()), NULL, N'TEST-GAP', 3, N'test');
                COMMIT;
                SELECT @first;
                """);
        }
        var (crashed, clean, latest) = (first, first + 1, first + 2);
        var repository = new ApiRunRepository(new TestDbConnectionFactory());
        try
        {
            var runs = (await repository.GetRecentAsync(20)).Where(r => r.ApiRunKey is var k && (k == crashed || k == clean || k == latest)).ToDictionary(r => r.ApiRunKey);

            Assert.False(runs[clean].PreviousStoppedCleanly);
            Assert.Equal(runs[crashed].StartedAt, runs[clean].PreviousStoppedAt);
            Assert.True(runs[latest].PreviousStoppedCleanly);
            Assert.Equal(runs[clean].StoppedAt, runs[latest].PreviousStoppedAt);
        }
        finally
        {
            using var connection = new SqlConnection(TestDatabase.ConnectionString);
            await connection.ExecuteAsync("DELETE FROM web.api_run WHERE MachineName = N'TEST-GAP'");
        }
    }
}
