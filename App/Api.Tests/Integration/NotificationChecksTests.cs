using BlueTrack.Api.ApiAccess;
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
    private string? _originalSiteUrl;

    private readonly List<long> _pullKeys = [];

    private static readonly string[] NewTypes = [BlueTrack.Api.CyberArkDirect.SelfHostedPullFailedCheck.TypeName, NightlyImportFailedCheck.TypeName, OverdueRiskExceptionsCheck.TypeName, WeeklySummaryCheck.TypeName, ApiKeyExpiringCheck.TypeName, ApiKeyExpiredCheck.TypeName, SamlCertificateExpiringCheck.TypeName, CpmHealthCheck.TypeName];

    /// <summary>D-287: certificates by thumbprint, in place of the Windows store.</summary>
    private sealed class FakeCertificates : ISamlCertificateLookup
    {
        public Dictionary<string, System.Security.Cryptography.X509Certificates.X509Certificate2> ByThumbprint { get; } = new(StringComparer.OrdinalIgnoreCase);
        public System.Security.Cryptography.X509Certificates.X509Certificate2? Find(string thumbprint) => ByThumbprint.GetValueOrDefault(thumbprint);
    }

    private readonly FakeCertificates _certificates = new();

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
            services.RemoveAll<ISamlCertificateLookup>();
            services.AddSingleton<ISamlCertificateLookup>(_certificates);
        }));
    }

    public async Task InitializeAsync()
    {
        var repository = new NotificationRepository(new TestDbConnectionFactory());
        _originalSchedule = await repository.GetScheduleAsync();
        await repository.SaveScheduleAsync(new NotificationSchedule { DailyDigestTime = "07:00", WeeklySummaryDay = "Mon", WeeklySummaryTime = "07:00" }, null);
        _recipientKey = await repository.CreateRecipientAsync(new SaveNotificationRecipientRequest { Email = _recipient, IsActive = true });
        await SetEnabledAsync(false, NewTypes);
        _originalSiteUrl = await new AppConfigRepository(new TestDbConnectionFactory()).GetSiteUrlAsync();
        await SetSiteUrlAsync(null);
    }

    private static async Task SetSiteUrlAsync(string? siteUrl)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("UPDATE web.app_config SET SiteUrl = @siteUrl", new { siteUrl });
    }

    public async Task DisposeAsync()
    {
        var repository = new NotificationRepository(new TestDbConnectionFactory());
        if (_originalSchedule is not null) await repository.SaveScheduleAsync(_originalSchedule, null);
        await repository.DeleteRecipientAsync(_recipientKey);
        await SetEnabledAsync(false, NewTypes);
        await SetSiteUrlAsync(_originalSiteUrl);
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            DELETE nl FROM web.notification_log nl
            JOIN web.dim_notification_type nt ON nt.NotificationTypeKey = nl.NotificationTypeKey
            WHERE nt.NotificationTypeName IN @NewTypes AND nl.SentDate >= @Started;
            DELETE FROM dbo.import_log WHERE SourceFileName LIKE 'NotifyTest%';
            DELETE FROM dbo.etl_run_log WHERE EtlRunKey IN @Keys;
            DELETE FROM dbo.cyberark_direct_pull WHERE PullKey IN @PullKeys;
            """, new { NewTypes, Started = _startedUtc, Keys = _etlRunKeys.DefaultIfEmpty(-1), PullKeys = _pullKeys.DefaultIfEmpty(-1) });
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

    private async Task<long> InsertPullAsync(string status, int startedMinutesAgo, string? message = null, int? safes = 10, int? accounts = 100)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var key = await connection.QuerySingleAsync<long>("""
            INSERT INTO dbo.cyberark_direct_pull (StartedAt, CompletedAt, Status, TriggeredBy, Message, SafeCount, AccountCount)
            OUTPUT inserted.PullKey
            VALUES (DATEADD(MINUTE, -@startedMinutesAgo, SYSUTCDATETIME()), CASE WHEN @status = 'Started' THEN NULL ELSE SYSUTCDATETIME() END,
                    @status, 'schedule', @message, @safes, @accounts)
            """, new { status, startedMinutesAgo, message, safes, accounts });
        _pullKeys.Add(key);
        return key;
    }

    private static string PullMarker(long pullKey) => $"Pull: {pullKey}{Environment.NewLine}";

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
    public async Task NightlyImportFailed_CoversAFailedSnapshot_LinkingToTrendsWhenTheSiteUrlIsSet()
    {
        await SetEnabledAsync(true, NightlyImportFailedCheck.TypeName);
        await SetSiteUrlAsync("https://bluetrack-test.example.com");
        var runKey = await InsertRunAsync("Snapshot", "Failed", 5, "dim_date has no row for today");

        await NewService().RunOnceAsync(CancellationToken.None);

        var message = Assert.Single(MessagesFor(RunMarker(runKey)));
        Assert.Equal("BlueTrack: Snapshot failed", message.Subject);
        Assert.Contains("Snapshot (usp_RunDailySnapshot", message.Body);
        Assert.Contains("EXEC dbo.usp_RunDailySnapshot", message.Body);
        Assert.Contains("Reports > Trends (https://bluetrack-test.example.com/reports/trends)", message.Body);
        Assert.Contains("<a href=\"https://bluetrack-test.example.com/reports/trends\">Reports &gt; Trends</a>", message.Html);
    }

    [Fact]
    public async Task NightlyImportFailed_WithoutASiteUrl_NamesThePage_WithNoLink()
    {
        await SetEnabledAsync(true, NightlyImportFailedCheck.TypeName);
        var runKey = await InsertRunAsync("Load", "Failed", 5, "Deadlock");

        await NewService().RunOnceAsync(CancellationToken.None);

        var message = Assert.Single(MessagesFor(RunMarker(runKey)));
        Assert.Contains("In BlueTrack, open Admin > Data Sources > CyberArk Sources to check the CyberArk source settings.", message.Body);
        Assert.DoesNotContain("<a ", message.Html);
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

    /// <summary>
    /// D-287: the SAML provider's IdP and SP certificates, checked at the daily digest time: 30 days, 7 days, expired
    /// and not installed are each emailed once per certificate.
    /// </summary>
    [Fact]
    public async Task SamlCertificates_AreReported_At30And7Days_WhenExpired_AndWhenMissing_OnceEach()
    {
        await SetEnabledAsync(true, SamlCertificateExpiringCheck.TypeName);
        static System.Security.Cryptography.X509Certificates.X509Certificate2 Certificate(string subject, DateTimeOffset notAfter)
        {
            using var rsa = System.Security.Cryptography.RSA.Create(2048);
            var request = new System.Security.Cryptography.X509Certificates.CertificateRequest($"CN={subject}", rsa,
                System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
            return request.CreateSelfSigned(notAfter.AddYears(-1), notAfter);
        }
        var idp = Certificate("idp.example.com", new DateTimeOffset(2001, 1, 25, 0, 0, 0, TimeSpan.Zero));   // 22 full days left at 07:00 on 2 Jan
        _certificates.ByThumbprint[idp.Thumbprint] = idp;
        var missingSp = Guid.NewGuid().ToString("N").ToUpperInvariant()[..20];

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var saved = await connection.QuerySingleOrDefaultAsync<(int ProviderKey, bool IsEnabled, string? ConfigurationValues)?>(
            "SELECT ProviderKey, IsEnabled, ConfigurationValues FROM web.identity_provider_config WHERE ProviderType = 'SAML'");
        var config = System.Text.Json.JsonSerializer.Serialize(new { IdpCertificateThumbprint = idp.Thumbprint, SpCertificateThumbprint = missingSp });
        if (saved is null)
            await connection.ExecuteAsync("INSERT INTO web.identity_provider_config (ProviderType, DisplayName, IsEnabled, DisplayOrder, ConfigurationValues) VALUES ('SAML', 'SAML (test)', 1, 950, @config)", new { config });
        else
            await connection.ExecuteAsync("UPDATE web.identity_provider_config SET IsEnabled = 1, ConfigurationValues = @config WHERE ProviderType = 'SAML'", new { config });
        try
        {
            _clock.Set(new DateTimeOffset(2001, 1, 2, 7, 0, 0, TimeSpan.Zero));
            await NewService().RunOnceAsync(CancellationToken.None);
            await NewService().RunOnceAsync(CancellationToken.None);   // a "restart": nothing again

            var first = MessagesFor(idp.Thumbprint);
            Assert.Equal("BlueTrack: The SAML identity provider certificate expires in 22 days", Assert.Single(first).Subject);
            Assert.Contains(_recipient, first[0].To);
            var missing = Assert.Single(MessagesFor(missingSp));
            Assert.Equal("BlueTrack: The SAML BlueTrack SP certificate isn't installed", missing.Subject);

            _clock.Set(new DateTimeOffset(2001, 1, 20, 7, 0, 0, TimeSpan.Zero));   // 5 days left
            await NewService().RunOnceAsync(CancellationToken.None);
            Assert.Equal("BlueTrack: The SAML identity provider certificate expires in 4 day(s)", MessagesFor(idp.Thumbprint)[^1].Subject);
            _clock.Set(new DateTimeOffset(2001, 1, 21, 7, 0, 0, TimeSpan.Zero));
            await NewService().RunOnceAsync(CancellationToken.None);
            Assert.Equal(2, MessagesFor(idp.Thumbprint).Count);   // the 7-day email only once

            _clock.Set(new DateTimeOffset(2001, 1, 26, 7, 0, 0, TimeSpan.Zero));
            await NewService().RunOnceAsync(CancellationToken.None);
            Assert.Equal("BlueTrack: The SAML identity provider certificate has expired", MessagesFor(idp.Thumbprint)[^1].Subject);
            Assert.Single(MessagesFor(missingSp));
        }
        finally
        {
            if (saved is null)
                await connection.ExecuteAsync("DELETE FROM web.identity_provider_config WHERE ProviderType = 'SAML' AND DisplayName = 'SAML (test)'");
            else
                await connection.ExecuteAsync("UPDATE web.identity_provider_config SET IsEnabled = @IsEnabled, ConfigurationValues = @ConfigurationValues WHERE ProviderKey = @ProviderKey",
                    new { saved.Value.ProviderKey, saved.Value.IsEnabled, saved.Value.ConfigurationValues });
        }
    }

    /// <summary>D-288: the accounts that newly fail are emailed once, at the digest time; a day with none new sends nothing.</summary>
    [Fact]
    public async Task CpmHealth_EmailsTheNewlyFailingAccounts_Once()
    {
        await SetEnabledAsync(true, CpmHealthCheck.TypeName);
        var token = $"cpmnotify{Guid.NewGuid():N}"[..16];
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var accountKey = await connection.QuerySingleAsync<long>("""
            INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted, CPMStatus, CpmFailureSince, LastCPMAction, LastCPMActionResult)
            OUTPUT inserted.AccountKey VALUES (2, @token, @token, 0, 'failure', SYSUTCDATETIME(), 'Verify', 'Failure');
            """, new { token });
        try
        {
            _clock.Set(new DateTimeOffset(2001, 1, 2, 7, 0, 0, TimeSpan.Zero));
            await NewService().RunOnceAsync(CancellationToken.None);
            await NewService().RunOnceAsync(CancellationToken.None);
            var sent = Assert.Single(_sender.Messages, m => m.Subject.Contains("CPM status is now failure") && m.Body.Contains(token));
            Assert.Contains("Verify (Failure)", sent.Body);
            Assert.Contains(_recipient, sent.To);

            // The next day: the account still fails, but it isn't new.
            _clock.Set(new DateTimeOffset(2001, 1, 3, 7, 0, 0, TimeSpan.Zero));
            await NewService().RunOnceAsync(CancellationToken.None);
            Assert.Single(_sender.Messages, m => m.Body.Contains(token));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey = @accountKey", new { accountKey });
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

    /// <summary>
    /// D-243: the owner is emailed the lead time before the key expires and on the day; the admins (the type's
    /// recipients) on the day. A key with no owner or made for no more than the lead time is skipped for the owner;
    /// a disabled key for everyone. Once each, across a restart.
    /// </summary>
    [Fact]
    public async Task ApiKeyExpiry_EmailsTheOwnerAtTheLeadTimeAndOnTheDay_AndTheAdminsOnTheDay_OncePerKey()
    {
        await SetEnabledAsync(true, ApiKeyExpiringCheck.TypeName, ApiKeyExpiredCheck.TypeName);
        var keys = new ApiKeyRepository(new TestDbConnectionFactory());
        var creator = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var id = Guid.NewGuid().ToString("N")[..8];
        var owner = $"owner_{id}@example.com";
        var created = new List<int>();

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var roleKey = await connection.QuerySingleAsync<int>("SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Viewer'");
        async Task<string> AddKeyAsync(string label, string createdAt, string expiresAt, string? ownerEmail = null, bool enabled = true)
        {
            var name = $"NotifyTest {label} {id}";
            var (_, prefix, hash) = ApiKeyGenerator.Create();
            var key = await keys.CreateAsync(name, prefix, hash, roleKey, DateTime.UtcNow.AddYears(1), creator, ownerEmail);
            created.Add(key);
            await connection.ExecuteAsync("UPDATE web.api_key SET CreatedAt = @createdAt, ExpiresAt = @expiresAt, IsEnabled = @enabled WHERE ApiKeyKey = @key", new { createdAt, expiresAt, enabled, key });
            return name;
        }
        var originalLead = await connection.QuerySingleAsync<int>("SELECT ApiKeyReminderLeadDays FROM web.app_config");

        try
        {
            await connection.ExecuteAsync("UPDATE web.app_config SET ApiKeyReminderLeadDays = 7");
            var soon = await AddKeyAsync("A", "2000-10-01", "2001-01-08 23:59:59", owner);        // 7 days left on 2 Jan: the lead email
            var later = await AddKeyAsync("B", "2000-10-01", "2001-01-20 12:00:00", owner);       // 18 days left: not yet
            var noOwner = await AddKeyAsync("C", "2000-10-01", "2001-01-05", null);               // no owner: nobody at the lead time
            var shortLived = await AddKeyAsync("D", "2000-12-30", "2001-01-05", owner);           // made for 6 days (under the lead time): no lead email
            var expiredYesterday = await AddKeyAsync("E", "2000-10-01", "2001-01-01 07:00:00", owner);   // expired a day ago: the day's emails
            var disabled = await AddKeyAsync("F", "2000-10-01", "2001-01-01 08:00:00", owner, enabled: false);

            _clock.Set(new DateTimeOffset(2001, 1, 2, 6, 59, 0, TimeSpan.Zero));
            await NewService().RunOnceAsync(CancellationToken.None);
            Assert.Empty(MessagesFor(id));   // Before the digest time.

            _clock.Set(new DateTimeOffset(2001, 1, 2, 7, 0, 0, TimeSpan.Zero));
            await NewService().RunOnceAsync(CancellationToken.None);
            await NewService().RunOnceAsync(CancellationToken.None);   // After a "restart": nothing is re-sent.

            var lead = Assert.Single(MessagesFor(soon));
            Assert.Equal($"BlueTrack: API key \"{soon}\" expires in 7 days", lead.Subject);
            Assert.Equal([owner], lead.To);   // The owner only, not the admins.
            Assert.Contains("Role: Viewer", lead.Body);
            Assert.Contains("Expires: 2001-01-08", lead.Body);
            Assert.NotNull(lead.Html);
            Assert.Empty(MessagesFor(later));
            Assert.Empty(MessagesFor(noOwner));
            Assert.Empty(MessagesFor(shortLived));
            Assert.Empty(MessagesFor(disabled));

            var expiredMessages = MessagesFor(expiredYesterday);
            Assert.Equal(2, expiredMessages.Count);
            Assert.Contains(expiredMessages, m => m.To.SequenceEqual([owner]) && m.Subject.EndsWith("has expired"));
            Assert.Contains(expiredMessages, m => m.To.Contains(_recipient) && !m.To.Contains(owner) && m.Body.Contains($"Its owner, {owner}, was told too."));

            // The next day the 7-day key has expired: its owner and the admins are told; the others are as before.
            _clock.Set(new DateTimeOffset(2001, 1, 9, 7, 0, 0, TimeSpan.Zero));
            await NewService().RunOnceAsync(CancellationToken.None);
            Assert.Equal(3, MessagesFor(soon).Count);   // lead, then the owner's and the admins' expiry emails
            Assert.Empty(MessagesFor(noOwner));   // C expired on the 5th: outside the 2 days, so nothing
            Assert.Equal(2, MessagesFor(expiredYesterday).Count);

            // A longer lead time (a setting) brings the reminder forward.
            await connection.ExecuteAsync("UPDATE web.app_config SET ApiKeyReminderLeadDays = 15");
            _clock.Set(new DateTimeOffset(2001, 1, 10, 7, 0, 0, TimeSpan.Zero));
            await NewService().RunOnceAsync(CancellationToken.None);
            Assert.Contains("expires in 11 days", Assert.Single(MessagesFor(later)).Subject);
        }
        finally
        {
            await connection.ExecuteAsync("UPDATE web.app_config SET ApiKeyReminderLeadDays = @originalLead", new { originalLead });
            if (created.Count > 0) await connection.ExecuteAsync("DELETE FROM web.api_key WHERE ApiKeyKey IN @created", new { created });
        }
    }

    // ---- D-250: Self-Hosted Direct pull alerts ----

    [Fact]
    public async Task SelfHostedPullFailed_MailsARejectedPullOnce_WithTheMessagesAndTheBeforeAndAfterCounts()
    {
        await SetEnabledAsync(true, BlueTrack.Api.CyberArkDirect.SelfHostedPullFailedCheck.TypeName);
        await SetSiteUrlAsync("https://bluetrack-test.example.com");
        var good = await InsertPullAsync("Succeeded", 600, null, 40, 2452);
        var rejected = await InsertPullAsync("Rejected", 10, "Safes went from 40 to 10 (-30; at most 8 allowed at 20%) | Safes no longer returned (30): <b>Alpha</b>, Beta", 10, 100);

        await NewService().RunOnceAsync(CancellationToken.None);
        await NewService().RunOnceAsync(CancellationToken.None);   // not re-sent

        Assert.Empty(MessagesFor(PullMarker(good)));   // a success is never mailed
        var message = Assert.Single(MessagesFor(PullMarker(rejected)));
        Assert.Equal("BlueTrack: Self-Hosted Direct pull was rejected", message.Subject);
        Assert.Contains(_recipient, message.To);
        Assert.Contains("Safes went from 40 to 10", message.Body);
        Assert.Contains("Safes no longer returned (30)", message.Body);
        Assert.Contains("40 before, 10 now", message.Body);
        Assert.Contains("2452 before, 100 now", message.Body);
        Assert.Contains("Accept the change and pull", message.Body);
        Assert.Contains("Admin > Data Sources (https://bluetrack-test.example.com/admin/data-sources)", message.Body);
        Assert.Contains("&lt;b&gt;Alpha&lt;/b&gt;", message.Html);   // escaped
        Assert.DoesNotContain("<b>Alpha</b>", message.Html);

        var repository = new NotificationRepository(new TestDbConnectionFactory());
        Assert.True(await repository.WasSentAsync(BlueTrack.Api.CyberArkDirect.SelfHostedPullFailedCheck.TypeName, BlueTrack.Api.CyberArkDirect.SelfHostedPullFailedCheck.DedupKeyFor(rejected)));
    }

    [Fact]
    public async Task SelfHostedPullFailed_MailsAFailedPull_AndAPullStillStartedAfterTwoHours_ButNotARunningOne()
    {
        await SetEnabledAsync(true, BlueTrack.Api.CyberArkDirect.SelfHostedPullFailedCheck.TypeName);
        var failed = await InsertPullAsync("Failed", 5, "PVWA refused to log on (401): the credential was refused.", null, null);
        var stuck = await InsertPullAsync("Started", 3 * 60, null, null, null);
        var running = await InsertPullAsync("Started", 30, null, null, null);

        await NewService().RunOnceAsync(CancellationToken.None);

        var failedMessage = Assert.Single(MessagesFor(PullMarker(failed)));
        Assert.Equal("BlueTrack: Self-Hosted Direct pull failed", failedMessage.Subject);
        Assert.Contains("the credential was refused", failedMessage.Body);
        Assert.DoesNotContain("before, ", failedMessage.Body);   // a failed pull has no counts to compare
        Assert.Equal("BlueTrack: Self-Hosted Direct pull has not finished", Assert.Single(MessagesFor(PullMarker(stuck))).Subject);
        Assert.Empty(MessagesFor(PullMarker(running)));
    }

    [Fact]
    public async Task SelfHostedPullFailed_ADisabledTypeSendsNothing_AndAnOldPullIsIgnored()
    {
        var recent = await InsertPullAsync("Failed", 5, "boom");
        var old = await InsertPullAsync("Failed", 30 * 60, "old boom");

        await NewService().RunOnceAsync(CancellationToken.None);   // type disabled
        Assert.Empty(MessagesFor(PullMarker(recent)));

        await SetEnabledAsync(true, BlueTrack.Api.CyberArkDirect.SelfHostedPullFailedCheck.TypeName);
        await NewService().RunOnceAsync(CancellationToken.None);
        Assert.Single(MessagesFor(PullMarker(recent)));
        Assert.Empty(MessagesFor(PullMarker(old)));   // 30 hours old, past the 28-hour lookback
    }
}
