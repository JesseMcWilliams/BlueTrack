using BlueTrack.Api.Audit;
using BlueTrack.Api.Data;
using BlueTrack.Api.HealthChecks;
using BlueTrack.Api.Siem;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-234: the SIEM forwarder against BlueTrackTest and an in-process collector on 127.0.0.1 (TCP or TLS):
/// catching up after the collector was down, resuming after a restart, a transaction that commits after a
/// later row was read, the per-type filter, two senders and the lease, the purge keeping unsent rows, the
/// health check and the TLS trust modes. Each test starts forwarding "now" (no older rows) and turns it off
/// afterwards; the per-type settings are put back.
/// </summary>
[Collection("SiemForwarding")]
public sealed class SiemForwarderTests : IAsyncLifetime
{
    private readonly TestDbConnectionFactory _factory = new();
    private List<(int Key, bool IsForwarded, int CefSeverity)> _typeSettings = [];
    private int _userKey;
    private readonly string _marker = $"siemtest-{Guid.NewGuid():N}"[..20];

    public async Task InitializeAsync()
    {
        _userKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        _typeSettings = (await connection.QueryAsync<(int, bool, int)>(
            "SELECT AuditEventTypeKey, IsForwarded, CAST(CefSeverity AS INT) FROM web.siem_event_type_setting")).AsList();
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            UPDATE web.siem_forwarding_config SET IsEnabled = 0, Host = NULL, Port = 6514, Transport = 'Tls', Framing = 'OctetCounting',
                TlsTrustMode = 'System', TlsFingerprint = NULL, TlsCaCertificate = NULL, TlsCheckRevocation = 1, TimeoutSeconds = 10, MaxMessageBytes = 8192
            WHERE SiemForwardingConfigKey = 1;
            UPDATE web.siem_forwarding_state SET Watermark = NULL, LeaseOwner = NULL, LeaseExpiresAt = NULL, LastError = NULL, LastErrorAt = NULL,
                ConsecutiveFailures = 0 WHERE SiemForwardingStateKey = 1;
            """);
        foreach (var (key, isForwarded, severity) in _typeSettings)
        {
            await connection.ExecuteAsync("UPDATE web.siem_event_type_setting SET IsForwarded = @isForwarded, CefSeverity = @severity WHERE AuditEventTypeKey = @key",
                new { key, isForwarded, severity });
        }
        await connection.ExecuteAsync("""
            DELETE fc FROM web.audit_field_change fc JOIN web.audit_event e ON e.AuditEventKey = fc.AuditEventKey WHERE e.Detail LIKE @m;
            DELETE FROM web.audit_event WHERE Detail LIKE @m;
            """, new { m = "siemtest-%" });
    }

    /// <summary>Turns forwarding on to 127.0.0.1:port, starting after every row that exists now.</summary>
    private static async Task EnableAsync(int port, string transport = "Tcp", string framing = "OctetCounting", string trustMode = "System",
        string? fingerprint = null, string? ca = null, bool checkRevocation = false)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            UPDATE web.siem_forwarding_config SET IsEnabled = 1, Host = '127.0.0.1', Port = @port, Transport = @transport, Framing = @framing,
                TlsTrustMode = @trustMode, TlsFingerprint = @fingerprint, TlsCaCertificate = @ca, TlsCheckRevocation = @checkRevocation,
                TimeoutSeconds = 5, MaxMessageBytes = 8192, Facility = 20, AppName = 'BlueTrack', SyslogHostName = 'bt-test'
            WHERE SiemForwardingConfigKey = 1;
            UPDATE web.siem_forwarding_state SET Watermark = CAST(MIN_ACTIVE_ROWVERSION() AS BIGINT) - 1, LeaseOwner = NULL, LeaseExpiresAt = NULL,
                LastError = NULL, LastErrorAt = NULL, ConsecutiveFailures = 0
            WHERE SiemForwardingStateKey = 1;
            """, new { port, transport, framing, trustMode, fingerprint, ca, checkRevocation });
    }

    private async Task<long> InsertAsync(string eventType = "FieldEdit", string? suffix = null, DateTime? occurredAt = null, SqlConnection? connection = null,
        SqlTransaction? transaction = null)
    {
        var own = connection is null;
        connection ??= new SqlConnection(TestDatabase.ConnectionString);
        try
        {
            return await connection.QuerySingleAsync<long>("""
                INSERT INTO web.audit_event (AuditEventTypeKey, PerformedByUserKey, OccurredAt, EntityName, EntityKey, SourceIpAddress, Detail)
                OUTPUT inserted.AuditEventKey
                SELECT AuditEventTypeKey, @userKey, ISNULL(@occurredAt, SYSUTCDATETIME()), 'siem_test', @suffix, '127.0.0.1', @detail
                FROM web.dim_audit_event_type WHERE EventTypeName = @eventType
                """, new { userKey = _userKey, occurredAt, suffix, detail = $"{_marker} {suffix}", eventType }, transaction);
        }
        finally
        {
            if (own) await connection.DisposeAsync();
        }
    }

    private SiemForwarder Forwarder(string owner, int batchSize = 100, TimeSpan? lease = null) =>
        new(_factory, new SyslogTransport(), NullLogger<SiemForwarder>.Instance,
            new SiemForwarderOptions { BatchSize = batchSize, LeaseDuration = lease }, owner);

    /// <summary>Runs passes until one is idle (or not the lease holder).</summary>
    private static async Task DrainAsync(SiemForwarder forwarder)
    {
        for (var i = 0; i < 50; i++)
        {
            var result = await forwarder.RunOnceAsync(CancellationToken.None);
            if (result.Outcome != SiemPassOutcome.Sent) return;
        }
    }

    private List<string> Ours(IEnumerable<string> messages) => messages.Where(m => m.Contains(_marker)).ToList();

    private static string Suffix(string message)
    {
        var at = message.IndexOf("cs2=", StringComparison.Ordinal) + 4;
        return message[at..message.IndexOf(' ', at)];
    }

    private static async Task<SiemForwardingStatus> StatusAsync() => await new SiemForwardingRepository(new TestDbConnectionFactory()).GetStatusAsync();

    [Fact]
    public async Task SendsNewRowsOnly_InOrder_AsRfc5424Cef()
    {
        var before = await InsertAsync(suffix: "before");
        await using var listener = new SyslogTestListener();
        await EnableAsync(listener.Port);
        var a = await InsertAsync(suffix: "a");
        var b = await InsertAsync("ReportExport", "b");

        var result = await Forwarder("t-order").RunOnceAsync(CancellationToken.None);
        Assert.Equal(SiemPassOutcome.Sent, result.Outcome);

        var messages = Ours(await listener.WaitForAsync(2));
        Assert.Equal(["a", "b"], messages.Select(Suffix));
        Assert.StartsWith("<166>1 ", messages[0]);   // local4 (20) * 8 + Informational (6): FieldEdit is CEF 3
        Assert.Contains(" bt-test BlueTrack - FieldEdit - CEF:0|BlueTrack|BlueTrack|", messages[0]);
        Assert.StartsWith("<165>1 ", messages[1]);   // ReportExport is CEF 5: Notice (5)
        Assert.Contains($"externalId={a} ", messages[0]);
        Assert.Contains("|ReportExport|", messages[1]);
        Assert.DoesNotContain(messages, m => m.Contains($"externalId={before} "));
        var status = await StatusAsync();
        Assert.Equal(b, status.LastSentAuditEventKey);
        Assert.Null(status.LastError);
    }

    [Fact]
    public async Task CollectorDown_FailsAndKeepsRows_ThenCatchesUp_AndTheHealthCheckFollows()
    {
        var port = SyslogTestListener.FreePort();
        await EnableAsync(port);
        var keys = new List<long>();
        for (var i = 0; i < 3; i++) keys.Add(await InsertAsync(suffix: $"down{i}"));

        var forwarder = Forwarder("t-down");
        var failed = await forwarder.RunOnceAsync(CancellationToken.None);
        Assert.Equal(SiemPassOutcome.Failed, failed.Outcome);
        var status = await StatusAsync();
        Assert.NotNull(status.LastError);
        Assert.Equal(1, status.ConsecutiveFailures);
        Assert.Equal(3, status.PendingCount);

        var health = new SiemForwardingHealthCheck(new SiemForwardingRepository(_factory), TimeProvider.System);
        Assert.Equal(HealthStatus.Degraded, (await health.CheckHealthAsync(new HealthCheckContext())).Status);

        await using var listener = new SyslogTestListener(port: port);
        await DrainAsync(forwarder);
        Assert.Equal(["down0", "down1", "down2"], Ours(await listener.WaitForAsync(3)).Select(Suffix));
        status = await StatusAsync();
        Assert.Null(status.LastError);
        Assert.Equal(0, status.PendingCount);
        Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(new HealthCheckContext())).Status);
    }

    [Fact]
    public async Task HealthCheck_IsDegraded_WhenTheOldestUnsentRowIsOlderThan15Minutes()
    {
        var port = SyslogTestListener.FreePort();
        await EnableAsync(port);
        await InsertAsync(suffix: "late", occurredAt: DateTime.UtcNow.AddMinutes(-20));
        var health = new SiemForwardingHealthCheck(new SiemForwardingRepository(_factory), TimeProvider.System);
        var result = await health.CheckHealthAsync(new HealthCheckContext());
        Assert.Equal(HealthStatus.Degraded, result.Status);
        Assert.Contains("wait to be forwarded", result.Description);

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("UPDATE web.siem_forwarding_config SET IsEnabled = 0");
        Assert.Equal(HealthStatus.Healthy, (await health.CheckHealthAsync(new HealthCheckContext())).Status);
    }

    [Fact]
    public async Task Restart_ResumesFromTheWatermark_WithoutResending()
    {
        await using var listener = new SyslogTestListener();
        await EnableAsync(listener.Port);
        for (var i = 0; i < 3; i++) await InsertAsync(suffix: $"r{i}");

        var first = Forwarder("t-restart-1", batchSize: 2);
        var pass = await first.RunOnceAsync(CancellationToken.None);
        Assert.Equal(2, pass.Sent);
        await first.ReleaseAsync();   // the API stopping

        var second = Forwarder("t-restart-2", batchSize: 2);   // the API starting again: a new process, a new owner
        await DrainAsync(second);
        Assert.Equal(["r0", "r1", "r2"], Ours(await listener.WaitForAsync(3)).Select(Suffix));
    }

    [Fact]
    public async Task ARowCommittedAfterALaterOneWasWritten_IsNotSkipped()
    {
        await using var listener = new SyslogTestListener();
        await EnableAsync(listener.Port);
        var forwarder = Forwarder("t-order-commit");

        await using var slow = new SqlConnection(TestDatabase.ConnectionString);
        await slow.OpenAsync();
        await using var transaction = (SqlTransaction)await slow.BeginTransactionAsync();
        var early = await InsertAsync(suffix: "early", connection: slow, transaction: transaction);   // lower key, not committed yet
        var late = await InsertAsync(suffix: "late");                                                  // higher key, committed
        Assert.True(early < late);

        // While the early row's transaction is open, nothing after it may be sent (an IDENTITY watermark would send
        // "late" and then never look back at "early").
        var pass = await forwarder.RunOnceAsync(CancellationToken.None);
        Assert.Equal(SiemPassOutcome.Idle, pass.Outcome);
        Assert.Empty(Ours(listener.Messages));

        await transaction.CommitAsync();
        await DrainAsync(forwarder);
        Assert.Equal(["early", "late"], Ours(await listener.WaitForAsync(2)).Select(Suffix));
    }

    [Fact]
    public async Task ARolledBackRow_DoesNotHoldUpLaterOnes()
    {
        await using var listener = new SyslogTestListener();
        await EnableAsync(listener.Port);
        await using (var slow = new SqlConnection(TestDatabase.ConnectionString))
        {
            await slow.OpenAsync();
            await using var transaction = (SqlTransaction)await slow.BeginTransactionAsync();
            await InsertAsync(suffix: "gone", connection: slow, transaction: transaction);
            await transaction.RollbackAsync();
        }
        await InsertAsync(suffix: "after");
        await DrainAsync(Forwarder("t-rollback"));
        Assert.Equal(["after"], Ours(await listener.WaitForAsync(1)).Select(Suffix));
    }

    [Fact]
    public async Task ATypeSwitchedOff_IsSkipped_AndTheWatermarkMovesPastIt()
    {
        await using var listener = new SyslogTestListener();
        await EnableAsync(listener.Port);
        await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
        {
            await connection.ExecuteAsync("""
                UPDATE s SET IsForwarded = 0 FROM web.siem_event_type_setting s
                JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = s.AuditEventTypeKey WHERE t.EventTypeName = 'FieldEdit'
                """);
        }
        await InsertAsync("FieldEdit", "off");
        var exported = await InsertAsync("ReportExport", "on");
        await InsertAsync("FieldEdit", "off2");

        var pass = await Forwarder("t-filter").RunOnceAsync(CancellationToken.None);
        Assert.Equal(1, pass.Sent);
        Assert.Equal(2, pass.Skipped);
        Assert.Equal(["on"], Ours(await listener.WaitForAsync(1)).Select(Suffix));
        var status = await StatusAsync();
        Assert.Equal(exported, status.LastSentAuditEventKey);
        Assert.Equal(0, status.PendingCount);
    }

    [Fact]
    public async Task TwoSenders_OnlyTheLeaseHolderSends_AndTheOtherTakesOverWhenItExpires()
    {
        await using var listener = new SyslogTestListener();
        await EnableAsync(listener.Port);
        for (var i = 0; i < 6; i++) await InsertAsync(suffix: $"l{i}");

        var a = Forwarder("t-lease-a", batchSize: 2, lease: TimeSpan.FromSeconds(3));
        var b = Forwarder("t-lease-b", batchSize: 2, lease: TimeSpan.FromSeconds(3));

        // Both run at once, repeatedly: every row arrives exactly once.
        for (var i = 0; i < 3; i++)
        {
            var results = await Task.WhenAll(a.RunOnceAsync(CancellationToken.None), b.RunOnceAsync(CancellationToken.None));
            Assert.Contains(results, r => r.Outcome == SiemPassOutcome.NotLeaseHolder);
        }
        Assert.Equal(["l0", "l1", "l2", "l3", "l4", "l5"], Ours(await listener.WaitForAsync(6)).Select(Suffix));

        // a "crashes" holding the lease; b can't send until it expires, then carries on.
        var holder = (await StatusAsync()).LeaseOwner == a.OwnerId ? a : b;
        var other = holder == a ? b : a;
        await InsertAsync(suffix: "l6");
        Assert.Equal(SiemPassOutcome.NotLeaseHolder, (await other.RunOnceAsync(CancellationToken.None)).Outcome);
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        Assert.Equal(SiemPassOutcome.Sent, (await other.RunOnceAsync(CancellationToken.None)).Outcome);
        Assert.Equal(7, Ours(await listener.WaitForAsync(7)).Count);
    }

    [Fact]
    public async Task Purge_KeepsOldRowsNotYetForwarded_AndDeletesThemOnceSent()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var originalRetention = await connection.QuerySingleOrDefaultAsync<int?>("SELECT RetentionDays FROM web.audit_config");
        var port = SyslogTestListener.FreePort();
        await EnableAsync(port);
        var old = await InsertAsync(suffix: "old", occurredAt: DateTime.UtcNow.AddDays(-40));
        await connection.ExecuteAsync("INSERT INTO web.audit_field_change (AuditEventKey, FieldName, OldValue, NewValue) VALUES (@old, 'F', '1', '2')", new { old });
        try
        {
            await connection.ExecuteAsync("UPDATE web.audit_config SET RetentionDays = 30");
            await connection.ExecuteAsync("dbo.usp_PurgeAuditLog", commandType: System.Data.CommandType.StoredProcedure);
            Assert.Equal(1, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.audit_event WHERE AuditEventKey = @old", new { old }));
            Assert.Equal(1, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.audit_field_change WHERE AuditEventKey = @old", new { old }));

            await using var listener = new SyslogTestListener(port: port);
            await DrainAsync(Forwarder("t-purge"));
            var sent = Ours(await listener.WaitForAsync(1));
            Assert.Single(sent);
            Assert.Contains("cs3=F: \"1\" -> \"2\" cs3Label=Field Changes", sent[0]);

            await connection.ExecuteAsync("dbo.usp_PurgeAuditLog", commandType: System.Data.CommandType.StoredProcedure);
            Assert.Equal(0, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.audit_event WHERE AuditEventKey = @old", new { old }));
        }
        finally
        {
            await connection.ExecuteAsync("UPDATE web.audit_config SET RetentionDays = @originalRetention", new { originalRetention });
        }
    }

    [Fact]
    public async Task AuditLogger_EventAndFieldChangesArriveTogether()
    {
        await using var listener = new SyslogTestListener(framing: "LineFeed");
        await EnableAsync(listener.Port, framing: "LineFeed");
        var logger = new AuditLogger(_factory, new HttpContextAccessor(), new AppConfigRepository(_factory));
        await logger.LogAsync("FieldEdit", _userKey, "siem_test", "fc", detail: $"{_marker} fc", reason: "Because=yes",
            fieldChanges: [new FieldChange("Stage", "Discovered", "Onboarded"), new FieldChange("Note", null, "line1\nline2")]);

        await DrainAsync(Forwarder("t-logger"));
        var message = Assert.Single(Ours(await listener.WaitForAsync(1)));
        Assert.Contains("cs3=Stage: \"Discovered\" -> \"Onboarded\"; Note: (empty) -> \"line1\\nline2\"", message);
        Assert.Contains(@"reason=Because\=yes", message);
    }

    [Fact]
    public async Task Tls_WithTheCaGiven_SendsAndChecksTheChain()
    {
        using var certificates = new TestCertificates();
        await using var listener = new SyslogTestListener(certificate: certificates.Server);
        await EnableAsync(listener.Port, transport: "Tls", trustMode: "CaCertificate", ca: certificates.CaPem);
        await InsertAsync(suffix: "tls-ca");
        await DrainAsync(Forwarder("t-tls-ca"));
        Assert.Equal(["tls-ca"], Ours(await listener.WaitForAsync(1)).Select(Suffix));
    }

    [Fact]
    public async Task Tls_WithThePinnedFingerprint_Sends_AndAWrongPinFails()
    {
        using var certificates = new TestCertificates();
        await using var listener = new SyslogTestListener(certificate: certificates.Server);
        var pin = SiemForwardingValidation.Fingerprint(certificates.Server, "SHA256");
        await EnableAsync(listener.Port, transport: "Tls", trustMode: "Fingerprint", fingerprint: pin);
        await InsertAsync(suffix: "tls-pin");
        await DrainAsync(Forwarder("t-tls-pin"));
        Assert.Equal(["tls-pin"], Ours(await listener.WaitForAsync(1)).Select(Suffix));

        await EnableAsync(listener.Port, transport: "Tls", trustMode: "Fingerprint", fingerprint: "SHA256:" + new string('A', 64));
        await InsertAsync(suffix: "tls-wrong-pin");
        var pass = await Forwarder("t-tls-pin2").RunOnceAsync(CancellationToken.None);
        Assert.Equal(SiemPassOutcome.Failed, pass.Outcome);
        Assert.Single(Ours(listener.Messages));
    }

    [Fact]
    public async Task Tls_SystemTrust_RefusesACollectorWhoseCaIsNotTrusted()
    {
        using var certificates = new TestCertificates();
        await using var listener = new SyslogTestListener(certificate: certificates.Server);
        await EnableAsync(listener.Port, transport: "Tls", trustMode: "System");
        await InsertAsync(suffix: "tls-system");
        var pass = await Forwarder("t-tls-system").RunOnceAsync(CancellationToken.None);
        Assert.Equal(SiemPassOutcome.Failed, pass.Outcome);
        Assert.Empty(Ours(listener.Messages));
        Assert.Equal(1, (await StatusAsync()).PendingCount);
    }

    [Fact]
    public async Task Tls_CaMode_WithRevocationChecking_FailsWhenRevocationCannotBeChecked()
    {
        // The test certificate has no CRL distribution point, so its revocation status is unknown.
        using var certificates = new TestCertificates();
        await using var listener = new SyslogTestListener(certificate: certificates.Server);
        await EnableAsync(listener.Port, transport: "Tls", trustMode: "CaCertificate", ca: certificates.CaPem, checkRevocation: true);
        await InsertAsync(suffix: "tls-revocation");
        var pass = await Forwarder("t-tls-rev").RunOnceAsync(CancellationToken.None);
        Assert.Equal(SiemPassOutcome.Failed, pass.Outcome);
        Assert.Empty(Ours(listener.Messages));
    }
}
