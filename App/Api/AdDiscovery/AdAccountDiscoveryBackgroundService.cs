using BlueTrack.Api.Data;
using BlueTrack.Api.DataFeeds;

namespace BlueTrack.Api.AdDiscovery;

/// <summary>
/// AD Account Discovery feature (2026-09-16): runs AdAccountDiscoveryService nightly. D-317: at
/// web.app_config.AdDiscoveryRunTime (default 04:30), the same way DataFeedBackgroundService runs the feeds -- it
/// wakes every minute and re-reads the time, so a change on Data Sources applies without a restart -- instead of
/// once a day counted from whenever the app started. Each run is recorded (AdDiscoveryRunner), and run records older
/// than DataFeedRunRetentionDays are purged after the scheduled run.
/// </summary>
public sealed class AdAccountDiscoveryBackgroundService(
    IServiceScopeFactory scopeFactory,
    AdDiscoveryRunner runner,
    TimeProvider timeProvider,
    ILogger<AdAccountDiscoveryBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<AdDiscoveryRunRepository>().CloseInterruptedAsync();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't close AD Account Discovery runs cut off by a restart.");
        }

        var lastWake = timeProvider.GetLocalNow().DateTime;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(WakeInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return; // Shutdown.
            }

            var now = timeProvider.GetLocalNow().DateTime;
            try
            {
                using var scope = scopeFactory.CreateScope();
                var runs = scope.ServiceProvider.GetRequiredService<AdDiscoveryRunRepository>();
                if (FeedSchedule.TryParseTime(await runs.GetRunTimeAsync(), out var runTime) && FeedSchedule.RunTimeFellBetween(lastWake, now, runTime))
                {
                    await runner.RunScheduledAsync(stoppingToken);
                    var retention = (await scope.ServiceProvider.GetRequiredService<AppConfigRepository>().GetAsync()).DataFeedRunRetentionDays;
                    await runs.PurgeAsync(retention);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "AD Account Discovery's schedule check failed -- will try again in a minute.");
            }
            lastWake = now;
        }
    }
}
