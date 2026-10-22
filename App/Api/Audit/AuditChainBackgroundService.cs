namespace BlueTrack.Api.Audit;

/// <summary>
/// D-295: seals new audit events within seconds. Every 5 s it runs a sealing pass; while full batches come back it
/// goes on after a short pause (the first run after the upgrade seals the whole existing log, oldest first, without
/// crowding out the pages). A failure (the
/// database briefly unavailable, say) is logged and retried; nothing is lost, since the events wait in the database.
/// </summary>
public sealed class AuditChainBackgroundService(AuditChainService chain, TimeProvider timeProvider, ILogger<AuditChainBackgroundService> logger)
    : BackgroundService
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan BacklogPause = TimeSpan.FromMilliseconds(250);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = PollInterval;
            try
            {
                if (await chain.SealOnceAsync() == AuditChainService.SealBatchSize) delay = BacklogPause;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogError(ex, "Audit chain sealing pass failed.");
                delay = TimeSpan.FromSeconds(30);
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
}
