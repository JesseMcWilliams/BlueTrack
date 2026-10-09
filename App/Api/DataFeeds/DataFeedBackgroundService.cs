using BlueTrack.Api.Data;

namespace BlueTrack.Api.DataFeeds;

/// <summary>
/// D-181: runs every enabled data feed once a day at web.app_config's
/// DataFeedRunTime (default 04:00, after the 02:00 Import+Load and 03:00
/// audit-purge Agent jobs), then purges run history older than
/// DataFeedRunRetentionDays. Same shape as AdAccountDiscoveryBackgroundService
/// -- a Singleton with a Task.Delay loop and its own DI scope -- except that
/// it wakes every few minutes and re-reads the run time, so a change on the
/// Global Application Configuration page applies without an app restart.
/// </summary>
public sealed class DataFeedBackgroundService(
    IServiceScopeFactory scopeFactory,
    DataFeedRunner runner,
    TimeProvider timeProvider,
    ILogger<DataFeedBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                var config = await scope.ServiceProvider.GetRequiredService<AppConfigRepository>().GetAsync();
                if (FeedSchedule.TryParseTime(config.DataFeedRunTime, out var runTime) && FeedSchedule.RunTimeFellBetween(lastWake, now, runTime))
                {
                    logger.LogInformation("Starting the nightly data feed run.");
                    await runner.RunScheduledAsync(stoppingToken);
                    var purged = await scope.ServiceProvider.GetRequiredService<DataFeedRepository>().PurgeRunsAsync(config.DataFeedRunRetentionDays);
                    logger.LogInformation("Nightly data feed run finished; purged {Count} old run records.", purged);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Nightly data feed run failed unexpectedly -- will try again tomorrow.");
            }
            lastWake = now;
        }
    }
}
