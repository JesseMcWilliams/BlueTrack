using BlueTrack.Api.Data;
using BlueTrack.Api.DataFeeds;

namespace BlueTrack.Api.CyberArkDirect;

/// <summary>
/// D-248: starts Self-Hosted Direct pulls. A Singleton, like DataFeedRunner, so its one-at-a-time gate spans the
/// nightly run and Run now. Each pull gets its own DI scope, since the pull service uses Scoped repositories.
/// D-275: the running pull, nightly or Run now, can be cancelled (<see cref="Cancel"/>).
/// D-302: a Pull now that succeeds then starts the Import and Load SQL Agent job; the nightly pull doesn't, since the
/// job's own schedule follows it.
/// </summary>
public sealed class SelfHostedDirectRunner(IServiceScopeFactory scopeFactory, IHostApplicationLifetime lifetime, ILogger<SelfHostedDirectRunner> logger,
    MemoryReleaser? memoryReleaser = null)
{
    private int _running;
    private CancellationTokenSource? _cancel;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>D-275: asks the running pull to stop; false if none is running. The pull logs itself as cancelled.</summary>
    public bool Cancel()
    {
        var cancel = Volatile.Read(ref _cancel);
        if (!IsRunning || cancel is null) return false;
        try
        {
            cancel.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;   // it finished in the meantime
        }
    }

    /// <summary>Starts a pull in the background and returns at once; false if one is already going.</summary>
    public bool TryStart(string triggeredBy, bool acceptChange)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunCoreAsync(triggeredBy, acceptChange, lifetime.ApplicationStopping, startLoad: true);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Self-Hosted Direct Run now failed unexpectedly.");
            }
            finally
            {
                Volatile.Write(ref _running, 0);
                memoryReleaser?.ReleaseSoon("a Self-Hosted Direct pull");   // D-371
            }
        });
        return true;
    }

    /// <summary>The nightly pull. Skips quietly if a Run now is going. D-367: <paramref name="triggeredBy"/> is "schedule (catch-up)" for a missed one.</summary>
    public async Task RunScheduledAsync(CancellationToken ct, string triggeredBy = "schedule")
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        try
        {
            await RunCoreAsync(triggeredBy, acceptChange: false, ct, startLoad: false);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
            memoryReleaser?.ReleaseSoon("a Self-Hosted Direct pull");   // D-371
        }
    }

    private async Task RunCoreAsync(string triggeredBy, bool acceptChange, CancellationToken ct, bool startLoad)
    {
        using var cancel = new CancellationTokenSource();
        Volatile.Write(ref _cancel, cancel);
        try
        {
            using var scope = scopeFactory.CreateScope();
            var outcome = await scope.ServiceProvider.GetRequiredService<SelfHostedDirectPullService>().RunAsync(triggeredBy, acceptChange, ct, cancel.Token);
            logger.LogInformation("Self-Hosted Direct pull ({TriggeredBy}): {Status}. {Message}", triggeredBy, outcome.Status, outcome.Message);
            if (startLoad && outcome is { Status: "Succeeded", PullKey: { } pullKey })
            {
                var error = await scope.ServiceProvider.GetRequiredService<SelfHostedDirectRepository>().StartImportLoadJobAsync(pullKey);
                if (error is null) logger.LogInformation("Started the Import and Load job after the pull.");
                else logger.LogWarning("Couldn't start the Import and Load job after the pull: {Error}", error);
            }
        }
        finally
        {
            Volatile.Write(ref _cancel, null);
        }
    }
}

/// <summary>
/// D-248: runs the Self-Hosted Direct pull once a night at the configured DirectPullTime, which must come before
/// the Import job's time (usp_Import_Configured rejects a pull older than 20 hours). Same shape as
/// DataFeedBackgroundService: wakes every minute and re-reads the setting, so a change on Admin > Data Sources
/// applies without a restart, and a time already past when the app starts doesn't fire until the next day.
/// D-367: except at the first wake, when a pull missed within the last 12 hours, with no Import since, is run then
/// (<see cref="MissedPullDetector.CatchUpDueAsync"/>).
/// </summary>
public sealed class SelfHostedDirectBackgroundService(
    IServiceScopeFactory scopeFactory,
    SelfHostedDirectRunner runner,
    HealthChecks.ApiRunRecorder apiRunRecorder,
    TimeProvider timeProvider,
    ILogger<SelfHostedDirectBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan WakeInterval = TimeSpan.FromMinutes(1);
    private const int KeepLogDays = 30;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastWake = timeProvider.GetLocalNow().DateTime;
        var catchUpChecked = false;
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
            await MarkCheckedAsync();
            try
            {
                using var scope = scopeFactory.CreateScope();
                var settings = (await scope.ServiceProvider.GetRequiredService<CyberArkSourceConfigRepository>().GetAsync()).Settings;
                var due = FeedSchedule.TryParseTime(settings.DirectPullTime, out var pullTime) && FeedSchedule.RunTimeFellBetween(lastWake, now, pullTime);
                if (due && SelfHostedDirectPullService.IsOn(settings))
                {
                    logger.LogInformation("Starting the nightly Self-Hosted Direct pull.");
                    await runner.RunScheduledAsync(stoppingToken);
                    await scope.ServiceProvider.GetRequiredService<SelfHostedDirectRepository>().PurgeAsync(KeepLogDays);
                }
                else if (!catchUpChecked && SelfHostedDirectPullService.IsOn(settings) && FeedSchedule.TryParseTime(settings.DirectPullTime, out var missedTime)
                         && await scope.ServiceProvider.GetRequiredService<MissedPullDetector>().CatchUpDueAsync(missedTime, now, timeProvider.LocalTimeZone) is { } missedAt)
                {
                    // D-367: the API started after tonight's pull time, which then waits a day, and the Import hasn't run yet.
                    logger.LogWarning("The Self-Hosted Direct pull due at {PullTime:yyyy-MM-dd HH:mm} didn't run (the API wasn't running then); starting it now.", missedAt);
                    await runner.RunScheduledAsync(stoppingToken, CatchUpTriggeredBy);
                }
                else if (due && settings.ImportPrivilegeCloud)
                {
                    // D-281: no pull to carry it, so Privilege Cloud's Safe groups are read from AD on their own.
                    var ad = await scope.ServiceProvider.GetRequiredService<SafeGroupAdExpansionService>().RunStandaloneAsync(stoppingToken);
                    if (ad is not null)
                    {
                        logger.LogInformation("Safe groups read from AD: {Found} found, {NotFound} not found, {Failed} failed.",
                            ad.Reads.Count(r => r.Status == "Found"), ad.Reads.Count(r => r.Status == "NotFound"), ad.Reads.Count(r => r.Status == "Failed"));
                    }
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
            catchUpChecked = true;
            lastWake = now;
        }
    }

    /// <summary>D-367: how a caught-up pull is shown in the pull log's By column.</summary>
    public const string CatchUpTriggeredBy = "schedule (catch-up)";

    /// <summary>
    /// D-362: records that the scheduler woke, on this run's web.api_run row, for Admin > Data Sources. Before the
    /// pull, so a long pull doesn't look like a stopped scheduler until it has run for a while. A failure is logged at
    /// Debug and never keeps the pull from running.
    /// </summary>
    private async Task MarkCheckedAsync()
    {
        if (apiRunRecorder.CurrentRunKey is not { } runKey) return;
        try
        {
            using var scope = scopeFactory.CreateScope();
            await scope.ServiceProvider.GetRequiredService<ApiRunRepository>().MarkPullSchedulerCheckedAsync(runKey);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Couldn't record that the pull scheduler checked.");
        }
    }
}
