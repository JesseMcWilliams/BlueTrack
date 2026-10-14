using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.Notifications;
using BlueTrack.Api.Tests.Contract;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-222: the three new notification checks, run through
/// NotificationCheckBackgroundService.RunOnceAsync against BlueTrackTest
/// with a fake sender and a manual clock (server local time = UTC here).
/// The app's own background service is removed, so only these cycles run.
/// A "restart" is a new service instance: nothing is carried in memory,
/// only web.notification_log.
/// </summary>
public class NotificationChecksTests : IClassFixture<BlueTrackWebApplicationFactory>, IAsyncLifetime
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly FakeSender _sender = new();
    private readonly ManualClock _clock = new(new DateTimeOffset(2001, 1, 1, 6, 0, 0, TimeSpan.Zero));
    private readonly string _recipient = $"notifytest_{Guid.NewGuid():N}@example.com";
    private int _recipientKey;
    private NotificationSchedule? _originalSchedule;
    private readonly DateTime _startedUtc = DateTime.UtcNow.AddMinutes(-1);
    private readonly List<long> _etlRunKeys = [];

    private static readonly string[] NewTypes = [NightlyImportFailedCheck.TypeName, OverdueRiskExceptionsCheck.TypeName, WeeklySummaryCheck.TypeName];

    public NotificationChecksTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            // BlueTrack's own background services go: this test drives the
            // notification cycle itself, and the manual clock's jumps must
            // not trigger a scheduled backup or data feed run.
            var background = services.Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType?.Assembly == typeof(NotificationCheckBackgroundService).Assembly).ToList();
            foreach (var descriptor in background) services.Remove(descriptor);
            services.RemoveAll<INotificationSender>();
            services.AddSingleton<INotificationSender>(_sender);
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(_clock);
        }));
    }

    public async Task InitializeAsync()
    {
        var repository = new NotificationRepository(new TestDbConnectionFactory());
        _originalSchedule = await repository.GetScheduleAsync();
        await repository.SaveScheduleAsync(new NotificationSchedule { DailyDigestTime = "07:00", WeeklySummaryDay = "Mon", WeeklySummaryTime = "07:00" }, null);
        _recipientKey = await repository.CreateRecipientAsync(new SaveNotificationRecipientRequest { Email = _recipient, IsActive = true });
        await SetEnabledAsync(false, NewTypes);
    }

    public async Task DisposeAsync()
    {
        var repository = new NotificationRepository(new TestDbConnectionFactory());
        if (_originalSchedule is not null) await repository.SaveScheduleAsync(_originalSchedule, null);
        await repository.DeleteRecipientAsync(_recipientKey);
        await SetEnabledAsync(false, NewTypes);
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            DELETE nl FROM web.notification_log nl
            JOIN web.dim_notification_type nt ON nt.NotificationTypeKey = nl.NotificationTypeKey
            WHERE nt.NotificationTypeName IN @NewTypes AND nl.SentDate >= @Started;
            DELETE FROM dbo.import_log WHERE SourceFileName LIKE 'NotifyTest%';
            DELETE FROM dbo.etl_run_log WHERE EtlRunKey IN @Keys;
            """, new { NewTypes, Started = _startedUtc, Keys = _etlRunKeys.DefaultIfEmpty(-1) });
        await _factory.DisposeAsync();
    }

    private NotificationCheckBackgroundService NewService() =>
        new(_factory.Services.GetRequiredService<IServiceScopeFactory>(), _clock, NullLogger<NotificationCheckBackgroundService>.Instance);

    private static async Task SetEnabledAsync(bool enabled, params string[] types)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("UPDATE web.dim_notification_type SET IsEnabled = @enabled WHERE NotificationTypeName IN @types", new { enabled, types });
    }

    private async Task<long> InsertRunAsync(string step, string status, int startedMinutesAgo, string? error = null)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var key = await connection.QuerySingleAsync<long>("""
            INSERT INTO dbo.etl_run_log (StepName, StartedAt, CompletedAt, Status, ErrorMessage, ErrorProcedure, ErrorLine)
            OUTPUT inserted.EtlRunKey
            VALUES (@step, DATEADD(MINUTE, -@startedMinutesAgo, SYSUTCDATETIME()),
                    CASE WHEN @status = 'Started' THEN NULL ELSE SYSUTCDATETIME() END,
                    @status, @error, CASE WHEN @error IS NULL THEN NULL ELSE 'usp_Import_PC_Accounts' END, CASE WHEN @error IS NULL THEN NULL ELSE 42 END)
            """, new { step, status, startedMinutesAgo, error });
        _etlRunKeys.Add(key);
        return key;
    }

    private static string RunMarker(long runKey) => $"Run: {runKey}{Environment.NewLine}";

    private IReadOnlyList<FakeSender.Message> MessagesFor(string dedupFragment) =>
        _sender.Messages.Where(m => m.Body.Contains(dedupFragment, StringComparison.Ordinal)).ToList();

    [Fact]
    public async Task NightlyImportFailed_SendsOncePerRun_NamingTheFileAndError_AcrossARestart()
    {
        await SetEnabledAsync(true, NightlyImportFailedCheck.TypeName);
        var runKey = await InsertRunAsync("Import", "Failed", 10, "Cannot bulk load <b>file</b> & \"quotes\"");
        await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
        {
            await connection.ExecuteAsync("""
                INSERT INTO dbo.import_log (SourceSystemKey, SourceFileName, StartedAt, CompletedAt, Status, ErrorMessage)
                SELECT TOP 1 SourceSystemKey, 'NotifyTest_<Accounts>.csv', DATEADD(MINUTE, -9, SYSUTCDATETIME()), SYSUTCDATETIME(), 'Failed', 'Cannot bulk load <b>file</b> & "quotes"'
                FROM dbo.dim_source_system ORDER BY SourceSystemKey
                """);
        }
        var marker = RunMarker(runKey);

        await NewService().RunOnceAsync(CancellationToken.None);
        await NewService().RunOnceAsync(CancellationToken.None);   // Same run again, after a "restart": not re-sent.

        var message = Assert.Single(MessagesFor(marker));
        Assert.Equal("BlueTrack: Import failed", message.Subject);
        Assert.Contains(_recipient, message.To);
        Assert.Contains("NotifyTest_<Accounts>.csv", message.Body);
        Assert.Contains("usp_Import_PC_Accounts, line 42", message.Body);
        Assert.NotNull(message.Html);
        Assert.Contains("NotifyTest_&lt;Accounts&gt;.csv", message.Html);
        Assert.Contains("Cannot bulk load &lt;b&gt;file&lt;/b&gt; &amp; &quot;quotes&quot;", message.Html);
        Assert.DoesNotContain("<b>file</b>", message.Html);

        var repository = new NotificationRepository(new TestDbConnectionFactory());
        Assert.True(await repository.WasSentAsync(NightlyImportFailedCheck.TypeName, NightlyImportFailedCheck.DedupKeyFor(runKey)));
    }

    [Fact]
    public async Task NightlyImportFailed_ReportsARunStillStartedAfterFourHours_ButNotARunningOne()
    {
        await SetEnabledAsync(true, NightlyImportFailedCheck.TypeName);
        var stuck = await InsertRunAsync("Load", "Started", 5 * 60);
        var running = await InsertRunAsync("Load", "Started", 30);
        var succeeded = await InsertRunAsync("Load", "Succeeded", 20);

        await NewService().RunOnceAsync(CancellationToken.None);

        var message = Assert.Single(MessagesFor(RunMarker(stuck)));
        Assert.Equal("BlueTrack: Load has not finished", message.Subject);
        Assert.Empty(MessagesFor(RunMarker(running)));
        Assert.Empty(MessagesFor(RunMarker(succeeded)));
    }

    [Fact]
    public async Task NightlyImportFailed_AFailedSendIsRetriedNextCycle_AndADisabledTypeSendsNothing()
    {
        var runKey = await InsertRunAsync("Load", "Failed", 5, "Deadlock");

        await NewService().RunOnceAsync(CancellationToken.None);   // Type still disabled.
        Assert.Empty(MessagesFor(RunMarker(runKey)));

        await SetEnabledAsync(true, NightlyImportFailedCheck.TypeName);
        _sender.FailNext = true;
        await NewService().RunOnceAsync(CancellationToken.None);
        Assert.Empty(MessagesFor(RunMarker(runKey)));
        var repository = new NotificationRepository(new TestDbConnectionFactory());
        Assert.False(await repository.WasSentAsync(NightlyImportFailedCheck.TypeName, NightlyImportFailedCheck.DedupKeyFor(runKey)));

        await NewService().RunOnceAsync(CancellationToken.None);
        Assert.Equal("BlueTrack: Load failed", Assert.Single(MessagesFor(RunMarker(runKey))).Subject);
    }

    [Fact]
    public async Task OverdueRiskExceptions_SentOnceADay_AtTheDigestTime_AcrossARestart()
    {
        await SetEnabledAsync(true, OverdueRiskExceptionsCheck.TypeName);
        var riskExceptions = new RiskExceptionRepository(new TestDbConnectionFactory());
        var exceptionKey = await riskExceptions.CreateAsync(new CreateRiskExceptionRequest
        {
            AccountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03"),
            Justification = "D-222 overdue digest <test> & co",
            ReviewDate = DateTime.UtcNow.Date.AddDays(-3)
        }, await TestUsers.GetUserKeyAsync("IntegrationTestUser1"));
        var exceptionId = (await riskExceptions.GetByKeyAsync(exceptionKey))!.ExceptionID + " | ";

        try
        {
            _clock.Set(new DateTimeOffset(2001, 1, 2, 6, 59, 0, TimeSpan.Zero));
            await NewService().RunOnceAsync(CancellationToken.None);
            Assert.Empty(MessagesFor(exceptionId));   // Before the digest time.

            _clock.Set(new DateTimeOffset(2001, 1, 2, 7, 0, 0, TimeSpan.Zero));
            await NewService().RunOnceAsync(CancellationToken.None);
            _clock.Set(new DateTimeOffset(2001, 1, 2, 18, 0, 0, TimeSpan.Zero));
            await NewService().RunOnceAsync(CancellationToken.None);   // Restart later the same day: not re-sent.

            var message = Assert.Single(MessagesFor(exceptionId));
            Assert.StartsWith("BlueTrack: ", message.Subject);
            Assert.Contains("overdue for review", message.Subject);
            Assert.Contains("Risk Exceptions > Overdue Reviews", message.Body);

            _clock.Set(new DateTimeOffset(2001, 1, 3, 7, 5, 0, TimeSpan.Zero));
            await NewService().RunOnceAsync(CancellationToken.None);
            Assert.Equal(2, MessagesFor(exceptionId).Count);   // The next day's digest.
        }
        finally
        {
            await riskExceptions.RevokeAsync(exceptionKey);
        }
    }

    [Fact]
    public async Task WeeklySummary_SentOnTheSetDayAndTime_OncePerWeek_SayingNoHistoryWhenThereIsNone()
    {
        await SetEnabledAsync(true, WeeklySummaryCheck.TypeName);
        int Weekly() => _sender.Messages.Count(m => m.Subject == "BlueTrack: weekly summary");

        _clock.Set(new DateTimeOffset(2001, 1, 1, 6, 59, 0, TimeSpan.Zero));   // Monday, before the time.
        await NewService().RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, Weekly());

        _clock.Set(new DateTimeOffset(2001, 1, 1, 7, 0, 0, TimeSpan.Zero));
        await NewService().RunOnceAsync(CancellationToken.None);
        await NewService().RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, Weekly());
        var message = _sender.Messages.Single(m => m.Subject == "BlueTrack: weekly summary");
        Assert.Contains("No history yet", message.Body);
        Assert.Contains("In Scope vs. All Accounts", message.Body);
        Assert.Contains("Risk Exceptions overdue for review", message.Body);
        Assert.Contains("Last Import and Load", message.Body);

        _clock.Set(new DateTimeOffset(2001, 1, 2, 7, 0, 0, TimeSpan.Zero));   // Tuesday.
        await NewService().RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, Weekly());

        _clock.Set(new DateTimeOffset(2001, 1, 8, 9, 0, 0, TimeSpan.Zero));   // The next Monday.
        await NewService().RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, Weekly());
    }

    [Fact]
    public async Task WeeklySummary_ComparesTheLatestSnapshotWithOneAWeekOlder()
    {
        await TrendsTestData.SeedAsync();
        try
        {
            await SetEnabledAsync(true, WeeklySummaryCheck.TypeName);
            _clock.Set(new DateTimeOffset(2026, 2, 16, 7, 30, 0, TimeSpan.Zero));   // Monday; snapshots on 02-02, 02-04 and 02-10.

            await NewService().RunOnceAsync(CancellationToken.None);

            var message = Assert.Single(_sender.Messages, m => m.Subject == "BlueTrack: weekly summary");
            Assert.Contains("Snapshot of 2026-02-10 compared with 2026-02-02.", message.Body);
            Assert.Contains("% complete: 0.0% to 66.7% (+66.7 points)", message.Body);
        }
        finally
        {
            await TrendsTestData.RemoveAsync();
        }
    }

    public sealed class FakeSender : INotificationSender
    {
        public sealed record Message(IReadOnlyList<string> To, string Subject, string Body, string? Html);

        private readonly List<Message> _messages = [];
        public bool FailNext { get; set; }

        public IReadOnlyList<Message> Messages { get { lock (_messages) return _messages.ToList(); } }

        public Task SendAsync(IReadOnlyList<string> toAddresses, string subject, string body, string? htmlBody = null)
        {
            if (FailNext)
            {
                FailNext = false;
                throw new InvalidOperationException("SMTP down (test)");
            }
            lock (_messages) _messages.Add(new Message(toAddresses, subject, body, htmlBody));
            return Task.CompletedTask;
        }
    }

    /// <summary>A settable clock whose local time zone is UTC, so "server local time" is predictable.</summary>
    public sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public void Set(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }
}
