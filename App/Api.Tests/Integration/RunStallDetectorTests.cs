using BlueTrack.Api.Data;
using BlueTrack.Api.HealthChecks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>D-348: what RunStallDetector calls stalled, and the Pull and Load health check built on it.</summary>
public class RunStallDetectorTests : IAsyncLifetime
{
    private readonly List<long> _pullKeys = [];
    private readonly List<long> _runKeys = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            DELETE FROM dbo.cyberark_direct_pull WHERE PullKey IN @pulls;
            DELETE FROM dbo.etl_run_log WHERE EtlRunKey IN @runs;
            """, new { pulls = _pullKeys.DefaultIfEmpty(-1), runs = _runKeys.DefaultIfEmpty(-1) });
    }

    private static RunStallDetector Detector()
    {
        var factory = new TestDbConnectionFactory();
        return new RunStallDetector(factory, new EtlRunRepository(factory));
    }

    private async Task<long> PullAsync(int startedMinutesAgo, int? progressMinutesAgo)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var key = await connection.QuerySingleAsync<long>("""
            INSERT INTO dbo.cyberark_direct_pull (StartedAt, Status, TriggeredBy, LastProgressAt)
            OUTPUT inserted.PullKey
            VALUES (DATEADD(MINUTE, -@startedMinutesAgo, SYSUTCDATETIME()), 'Started', 'schedule',
                    CASE WHEN @progressMinutesAgo IS NULL THEN NULL ELSE DATEADD(MINUTE, -@progressMinutesAgo, SYSUTCDATETIME()) END)
            """, new { startedMinutesAgo, progressMinutesAgo });
        _pullKeys.Add(key);
        return key;
    }

    [Fact]
    public async Task APull_IsStalled_WhenItsLastProgress_IsOlderThanTheStallTime()
    {
        var never = await PullAsync(60, null);
        var stopped = await PullAsync(60, 40);
        var reading = await PullAsync(60, 1);
        var young = await PullAsync(10, null);

        var report = await Detector().DetectAsync();

        var keys = report.Stalls.Where(s => s.Kind == "Pull").Select(s => s.DedupKey).ToHashSet();
        Assert.Contains($"pull:{never}", keys);
        Assert.Contains($"pull:{stopped}", keys);
        Assert.DoesNotContain($"pull:{reading}", keys);
        Assert.DoesNotContain($"pull:{young}", keys);
        Assert.Equal("It has read nothing since it started.", report.Stalls.Single(s => s.DedupKey == $"pull:{never}").Detail);
    }

    [Fact]
    public async Task ARunWhoseSessionEnded_IsStalled_AndTheHealthCheckIsUnhealthy()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var runKey = await connection.QuerySingleAsync<long>(
            "INSERT INTO dbo.etl_run_log (StepName, SessionId) VALUES ('Load', 32002); SELECT CAST(SCOPE_IDENTITY() AS BIGINT);");
        _runKeys.Add(runKey);

        var stall = (await Detector().DetectAsync()).Stalls.SingleOrDefault(s => s.DedupKey == $"run:{runKey}:ended");
        var canSeeSessions = (await new EtlRunRepository(new TestDbConnectionFactory()).GetRunningAsync())
            .Single(r => r.Activity.EtlRunKey == runKey).Activity.CanSeeSessions;
        if (!canSeeSessions) return;   // the session can't be looked up without the signed procedure's permission
        Assert.NotNull(stall);
        Assert.Contains("will never finish", stall.Detail);

        var factory = new TestDbConnectionFactory();
        var result = await new PullAndLoadHealthCheck(Detector(), factory).CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains($"run {runKey}", result.Description);
    }
}
