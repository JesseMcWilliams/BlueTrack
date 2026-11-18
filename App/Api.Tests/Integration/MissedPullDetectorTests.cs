using Dapper;
using BlueTrack.Api.CyberArkDirect;
using BlueTrack.Api.Data;
using BlueTrack.Api.Notifications;
using BlueTrack.Api.Tests.Contract;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-363: a nightly pull time that passed with no pull. The database cases use a pull time in 2031, after every real
/// pull and API start in the test database, with their own web.api_run rows (MachineName TEST-MISSED), so they don't
/// depend on the shared Self-Hosted settings or on other tests' rows.
/// </summary>
public class MissedPullDetectorTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;
    private static readonly TimeOnly PullTime = new(1, 0);
    private static readonly DateTime PullAt = new(2031, 3, 2, 1, 0, 0);
    private static readonly DateTime Now = new(2031, 3, 2, 9, 0, 0);

    public MissedPullDetectorTests(BlueTrackWebApplicationFactory factory) => _factory = factory;

    private static MissedPullDetector Detector() =>
        new(new TestDbConnectionFactory(), new CyberArkSourceConfigRepository(new TestDbConnectionFactory()), TimeProvider.System);

    private static async Task InsertRunAsync(DateTime startedAt, DateTime? stoppedAt, DateTime? checkedAt)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            INSERT INTO web.api_run (StartedAt, StoppedAt, PullSchedulerCheckedAt, MachineName, ProcessId, Version)
            VALUES (@startedAt, @stoppedAt, @checkedAt, N'TEST-MISSED', 1, N'test')
            """, new { startedAt, stoppedAt, checkedAt });
    }

    private static async Task CleanUpAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("DELETE FROM web.api_run WHERE MachineName = N'TEST-MISSED'");
    }

    [Theory]
    [InlineData("2026-11-18 09:00", "2026-11-18 01:00")]   // today's time has passed
    [InlineData("2026-11-18 01:05", "2026-11-17 01:00")]   // inside the grace: yesterday's
    [InlineData("2026-11-18 01:10", "2026-11-18 01:00")]   // the grace has just ended
    [InlineData("2026-11-18 00:30", "2026-11-17 01:00")]   // before today's time
    public void LatestPassed_IsTheLatestPullTimeAtLeastTheGraceAgo(string now, string expected) =>
        Assert.Equal(DateTime.Parse(expected), MissedPullDetector.LatestPassed(PullTime, DateTime.Parse(now)));

    [Fact]
    public async Task ApiStoppedBeforeThePullTime_IsNotRunning_WithWhenItStartedAgain()
    {
        try
        {
            await InsertRunAsync(PullAt.AddDays(-1), PullAt.AddHours(-7), PullAt.AddHours(-7));
            await InsertRunAsync(PullAt.AddHours(7), null, Now);

            var missed = await Detector().DetectAsync(PullTime, Now, TimeZoneInfo.Utc);

            Assert.NotNull(missed);
            Assert.Equal((PullAt, PullAt, MissedPullDetector.ApiState.NotRunning, (DateTime?)PullAt.AddHours(7)),
                (missed!.ScheduledAtLocal, missed.ScheduledAtUtc, missed.Api, missed.ApiStartedAfterUtc));
            Assert.Equal("No Self-Hosted Direct pull started at 2031-03-02 01:00 (server time), so the next Import will fail. " +
                "The API wasn't running then; it started again at 2031-03-02 08:00. See API starts on Admin > Deployment.", missed.Describe(TimeZoneInfo.Utc));
        }
        finally
        {
            await CleanUpAsync();
        }
    }

    [Fact]
    public async Task SchedulerCheckedAfterThePullTime_IsRunning()
    {
        try
        {
            await InsertRunAsync(PullAt.AddDays(-1), null, PullAt.AddMinutes(5));

            var missed = await Detector().DetectAsync(PullTime, Now, TimeZoneInfo.Utc);

            Assert.Equal(MissedPullDetector.ApiState.Running, missed!.Api);
            Assert.Null(missed.ApiStartedAfterUtc);
            Assert.Contains("its pull scheduler didn't start the pull", missed.Describe(TimeZoneInfo.Utc));
        }
        finally
        {
            await CleanUpAsync();
        }
    }

    // D-367: catch up a missed pull when the API starts, only while tonight's Import hasn't run and within 12 hours.
    [Theory]
    [InlineData("2031-03-02 01:20", "2031-03-02 01:00")]   // started 20 minutes after the pull time
    [InlineData("2031-03-02 12:59", "2031-03-02 01:00")]   // just inside 12 hours
    [InlineData("2031-03-02 13:01", null)]                 // too long after
    [InlineData("2031-03-02 00:30", null)]                 // before today's time: yesterday's is 23.5 hours ago
    public async Task CatchUpDue_OnlyWithin12HoursOfThePullTime(string now, string? expected)
    {
        var due = await Detector().CatchUpDueAsync(PullTime, DateTime.Parse(now), TimeZoneInfo.Utc);
        Assert.Equal(expected is null ? null : DateTime.Parse(expected), due);
    }

    [Fact]
    public async Task CatchUpDue_NotOnceTheImportHasStartedSinceThePullTime()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var runKey = await connection.QuerySingleAsync<long>("""
            INSERT INTO dbo.etl_run_log (StepName, StartedAt, CompletedAt, Status) OUTPUT inserted.EtlRunKey
            VALUES (N'Import', '2031-03-02T01:30:00', '2031-03-02T01:31:00', N'Succeeded')
            """);
        try
        {
            Assert.Null(await Detector().CatchUpDueAsync(PullTime, new DateTime(2031, 3, 2, 2, 0, 0), TimeZoneInfo.Utc));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM dbo.etl_run_log WHERE EtlRunKey = @runKey", new { runKey });
        }
    }

    [Fact]
    public void Describe_WithNoRecordFromBefore_PointsAtApiStarts()
    {
        var missed = new MissedPullDetector.MissedPull(PullAt, PullAt, MissedPullDetector.ApiState.Unknown, null);
        Assert.EndsWith("so the next Import will fail. See API starts on Admin > Deployment.", missed.Describe(TimeZoneInfo.Utc));
    }

    [Fact]
    public async Task TheNotificationTypeIsSeeded_Off_AndItsCheckIsRegistered()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        Assert.False(await connection.QuerySingleAsync<bool>(
            "SELECT IsEnabled FROM web.dim_notification_type WHERE NotificationTypeName = @TypeName", new { SelfHostedPullMissedCheck.TypeName }));

        using var scope = _factory.Services.CreateScope();
        Assert.Contains(scope.ServiceProvider.GetServices<INotificationCheck>(), c => c is SelfHostedPullMissedCheck);
        Assert.Equal("missed:2031-03-02T01:00", SelfHostedPullMissedCheck.DedupKeyFor(PullAt));
    }
}
