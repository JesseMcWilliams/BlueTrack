using BlueTrack.Api.Data;
using BlueTrack.Api.DataFeeds;

namespace BlueTrack.Api.CyberArkDirect;

/// <summary>
/// D-248: starts Self-Hosted Direct pulls. A Singleton, like DataFeedRunner, so its one-at-a-time gate spans the
/// nightly run and Run now. Each pull gets its own DI scope, since the pull service uses Scoped repositories.
/// </summary>
public sealed class SelfHostedDirectRunner(IServiceScopeFactory scopeFactory, IHostApplicationLifetime lifetime, ILogger<SelfHostedDirectRunner> logger)
{
    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>Starts a pull in the background and returns at once; false if one is already going.</summary>
    public bool TryStart(string triggeredBy, bool acceptChange)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunCoreAsync(triggeredBy, acceptChange, lifetime.ApplicationStopping);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Self-Hosted Direct Run now failed unexpectedly.");
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        });
        return true;
    }

    /// <summary>The nightly pull. Skips quietly if a Run now is going.</summary>
    public async Task RunScheduledAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        try
        {
            await RunCoreAsync("schedule", acceptChange: false, ct);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task RunCoreAsync(string triggeredBy, bool acceptChange, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var outcome = await scope.ServiceProvider.GetRequiredService<SelfHostedDirectPullService>().RunAsync(triggeredBy, acceptChange, ct);
        logger.LogInformation("Self-Hosted Direct pull ({TriggeredBy}): {Status}. {Message}", triggeredBy, outcome.Status, outcome.Message);
    }
}

/// <summary>
/// D-248: runs the Self-Hosted Direct pull once a night at the configured DirectPullTime, which must come before
/// the Import job's time (usp_Import_Configured rejects a pull older than 20 hours). Same shape as
/// DataFeedBackgroundService: wakes every minute and re-reads the setting, so a change on Admin > Data Sources
/// applies without a restart, and a time already past when the app starts doesn't fire until the next day.
/// </summary>
public sealed class SelfHostedDirectBackgroundService(
    IServiceScopeFactory scopeFactory,
    SelfHostedDirectRunner runner,
    TimeProvider timeProvider,
    ILogger<SelfHostedDirectBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(1);
    private const int KeepLogDays = 30;

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
                var settings = (await scope.ServiceProvider.GetRequiredService<CyberArkSourceConfigRepository>().GetAsync()).Settings;
                if (settings.ImportSelfHosted && settings.SelfHostedSource == "Direct"
                    && FeedSchedule.TryParseTime(settings.DirectPullTime, out var pullTime) && FeedSchedule.RunTimeFellBetween(lastWake, now, pullTime))
                {
                    logger.LogInformation("Starting the nightly Self-Hosted Direct pull.");
                    await runner.RunScheduledAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<SelfHostedDirectRepository>().PurgeAsync(KeepLogDays);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Nightly Self-Hosted Direct pull failed unexpectedly -- will try again tomorrow.");
            }
            lastWake = now;
        }
    }
}
