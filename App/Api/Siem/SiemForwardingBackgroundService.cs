namespace BlueTrack.Api.Siem;

/// <summary>
/// D-234: forwards the audit log to the SIEM in near real time. Every PollInterval (5 s) it runs a
/// SiemForwarder pass; while full batches come back it goes straight on. After a failed send it waits
/// 5 s, 10 s, 20 s ... up to 5 minutes before trying again, and the rows stay waiting in the database, so
/// nothing is lost while the collector is down or the API restarts (the watermark is in
/// web.siem_forwarding_state). On shutdown it gives up its lease so another process can take over at once.
/// </summary>
public sealed class SiemForwardingBackgroundService(SiemForwarder forwarder, TimeProvider timeProvider, ILogger<SiemForwardingBackgroundService> logger)
    : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>5 s doubled per consecutive failure, at most MaxBackoff.</summary>
    public static TimeSpan Backoff(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0) return PollInterval;
        var seconds = PollInterval.TotalSeconds * Math.Pow(2, Math.Min(consecutiveFailures - 1, 16));
        return TimeSpan.FromSeconds(Math.Min(seconds, MaxBackoff.TotalSeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var failures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = PollInterval;
            try
            {
                var result = await forwarder.RunOnceAsync(stoppingToken);
                if (result.Outcome == SiemPassOutcome.Failed)
                {
                    failures++;
                    delay = Backoff(failures);
                }
                else
                {
                    failures = 0;
                    if (result.Outcome == SiemPassOutcome.Sent && result.BatchWasFull) delay = TimeSpan.Zero;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A database error (or anything else) must not stop the service; try again later.
                failures++;
                delay = Backoff(failures);
                logger.LogError(ex, "SIEM forwarding pass failed.");
            }

            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    // Shutting down.
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try
        {
            await forwarder.ReleaseAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SIEM forwarding: couldn't release the lease on shutdown; it expires on its own.");
        }
    }
}
