using System.Globalization;
using System.Text;
using BlueTrack.Api.Controllers;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Siem;

/// <summary>D-234: what one forwarding pass did.</summary>
public enum SiemPassOutcome
{
    /// <summary>Forwarding is off (or has no host).</summary>
    Disabled,
    /// <summary>Another process holds the lease.</summary>
    NotLeaseHolder,
    /// <summary>Nothing waiting.</summary>
    Idle,
    /// <summary>A batch was handled; more may be waiting.</summary>
    Sent,
    /// <summary>The send failed; the watermark didn't move.</summary>
    Failed,
    /// <summary>The lease was lost before the watermark moved; another sender may resend.</summary>
    LeaseLost
}

public sealed record SiemPassResult(SiemPassOutcome Outcome, int Sent = 0, int Skipped = 0, bool BatchWasFull = false, string? Error = null);

/// <summary>Tunables (tests shorten them).</summary>
public sealed class SiemForwarderOptions
{
    public int BatchSize { get; init; } = 100;
    public TimeSpan MinimumLease { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Tests only: a fixed lease length instead of the one worked out from the timeout.</summary>
    public TimeSpan? LeaseDuration { get; init; }
}

/// <summary>
/// D-234: one forwarding pass. Holds or takes the lease, reads the next batch after the watermark
/// (SiemForwardingRepository.ReadBatchAsync: WriteVersion order, committed rows only), sends the rows of
/// forwarded types as one connection's worth of syslog frames, then moves the watermark past the whole
/// batch -- only if the send worked and the lease is still ours. A failed send leaves the watermark where it
/// was, so the batch is sent again next time (at least once; never skipped). The owner ID is per process.
/// </summary>
public sealed class SiemForwarder(IDbConnectionFactory connectionFactory, ISyslogTransport transport, ILogger<SiemForwarder> logger,
    SiemForwarderOptions? options = null, string? ownerId = null)
{
    private readonly SiemForwardingRepository _repository = new(connectionFactory);
    private readonly SiemForwarderOptions _options = options ?? new SiemForwarderOptions();

    public string OwnerId { get; } = ownerId ?? $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}";

    public async Task<SiemPassResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = await _repository.GetSettingsAsync();
        if (!settings.IsEnabled || string.IsNullOrWhiteSpace(settings.Host))
        {
            await _repository.ReleaseLeaseAsync(OwnerId);
            return new SiemPassResult(SiemPassOutcome.Disabled);
        }

        if (!await _repository.TryAcquireLeaseAsync(OwnerId, LeaseDuration(settings)))
        {
            return new SiemPassResult(SiemPassOutcome.NotLeaseHolder);
        }

        var watermark = (await _repository.GetStatusWatermarkAsync()) ?? await _repository.EnsureWatermarkAsync();
        var rows = await _repository.ReadBatchAsync(watermark, _options.BatchSize);
        if (rows.Count == 0) return new SiemPassResult(SiemPassOutcome.Idle);

        var toSend = rows.Where(r => r.IsForwarded).ToList();
        var deviceVersion = AboutController.ProductVersion();
        var hostName = settings.SyslogHostName ?? SyslogFormatter.DefaultHostName();
        if (toSend.Count > 0)
        {
            var frames = toSend.Select(r => SyslogFormatter.Frame(BuildMessage(r, settings, deviceVersion, hostName), settings.Framing)).ToList();
            try
            {
                await transport.SendAsync(settings, frames, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var error = $"Sending to {settings.Host}:{settings.Port} ({settings.Transport}) failed: {Describe(ex)}";
                logger.LogWarning(ex, "SIEM forwarding: {Error}", error);
                await _repository.RecordFailureAsync(OwnerId, error);
                return new SiemPassResult(SiemPassOutcome.Failed, Error: error);
            }
        }

        var lastSent = toSend.Count > 0 ? toSend[^1].AuditEventKey : (long?)null;
        if (!await _repository.AdvanceAsync(OwnerId, watermark, rows[^1].WriteVersion, toSend.Count, lastSent))
        {
            logger.LogWarning("SIEM forwarding: the lease was lost before the watermark moved; another sender may resend {Count} events.", toSend.Count);
            return new SiemPassResult(SiemPassOutcome.LeaseLost);
        }
        return new SiemPassResult(SiemPassOutcome.Sent, toSend.Count, rows.Count - toSend.Count, rows.Count == _options.BatchSize);
    }

    public Task ReleaseAsync() => _repository.ReleaseLeaseAsync(OwnerId);

    /// <summary>Long enough to cover a whole send (connect, handshake, write, close) with room to spare.</summary>
    private TimeSpan LeaseDuration(SiemForwardingSettings settings)
    {
        if (_options.LeaseDuration is { } fixedLease) return fixedLease;
        var send = TimeSpan.FromSeconds(settings.TimeoutSeconds * 3 + 10);
        return send > _options.MinimumLease ? send : _options.MinimumLease;
    }

    /// <summary>The innermost useful message: "connection refused", a certificate failure, a timeout.</summary>
    public static string Describe(Exception ex)
    {
        var messages = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (!messages.Contains(e.Message)) messages.Add(e.Message);
        }
        return string.Join(" -- ", messages);
    }

    /// <summary>The RFC 5424 message for one audit row, kept within MaxMessageBytes (see FitToSize).</summary>
    public static string BuildMessage(SiemAuditRow row, SiemForwardingSettings settings, string deviceVersion, string hostName)
    {
        var cefEvent = ToCefEvent(row);
        string Wrap(CefEvent e) => SyslogFormatter.Message(settings.Facility, SyslogFormatter.SyslogSeverity(row.CefSeverity),
            row.OccurredAt, hostName, settings.AppName, row.EventTypeName, CefFormatter.Format(e, deviceVersion));
        return FitToSize(cefEvent, Wrap, settings.MaxMessageBytes);
    }

    /// <summary>
    /// If the message is over the limit: drop the field-change summary (cs3), then clip msg and reason to 256
    /// characters, then to 64. With the smallest limit (2048 bytes) the rest always fits.
    /// </summary>
    public static string FitToSize(CefEvent cefEvent, Func<CefEvent, string> wrap, int maxBytes)
    {
        var message = wrap(cefEvent);
        if (Encoding.UTF8.GetByteCount(message) <= maxBytes) return message;
        var smaller = cefEvent with { FieldChangeSummary = cefEvent.FieldChangeSummary is null ? null : "(omitted: too long)" };
        message = wrap(smaller);
        foreach (var length in new[] { 256, 64 })
        {
            if (Encoding.UTF8.GetByteCount(message) <= maxBytes) return message;
            smaller = smaller with
            {
                Message = smaller.Message is null ? null : CefFormatter.Clip(smaller.Message, length),
                Reason = smaller.Reason is null ? null : CefFormatter.Clip(smaller.Reason, length)
            };
            message = wrap(smaller);
        }
        return message;
    }

    public static CefEvent ToCefEvent(SiemAuditRow row) => new()
    {
        SignatureId = row.EventTypeName,
        Name = string.IsNullOrWhiteSpace(row.EventTypeDescription) ? row.EventTypeName : row.EventTypeDescription,
        Severity = row.CefSeverity,
        OccurredAtUtc = row.OccurredAt,
        ExternalId = row.AuditEventKey.ToString(CultureInfo.InvariantCulture),
        UserName = row.UserDisplayName ?? row.UserExternalIdentifier,
        UserId = row.UserExternalIdentifier,
        SourceAddress = row.SourceIpAddress,
        EntityName = row.EntityName,
        EntityKey = row.EntityKey,
        FieldChangeSummary = CefFormatter.SummarizeFieldChanges(row.FieldChanges),
        Reason = row.Reason,
        Message = row.Detail
    };

    /// <summary>The Test button's event: severity 1, not an audit row.</summary>
    public static string BuildTestMessage(SiemForwardingSettings settings, string deviceVersion, string hostName, string userName, DateTime nowUtc)
    {
        var cefEvent = new CefEvent
        {
            SignatureId = "SiemForwardingTest",
            Name = "SIEM forwarding test event",
            Severity = 1,
            OccurredAtUtc = nowUtc,
            UserName = userName,
            Message = "Test event from BlueTrack's Admin > SIEM Forwarding page. If the SIEM shows this, forwarding reaches it."
        };
        return SyslogFormatter.Message(settings.Facility, SyslogFormatter.SyslogSeverity(1), nowUtc, hostName, settings.AppName,
            cefEvent.SignatureId, CefFormatter.Format(cefEvent, deviceVersion));
    }
}
