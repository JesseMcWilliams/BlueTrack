namespace BlueTrack.Api.AdDiscovery;

/// <summary>
/// D-317: runs AD Account Discovery one at a time, for the schedule or Run now on Data Sources, and records each run
/// in web.ad_discovery_run. A Singleton, like SelfHostedDirectRunner, so its one-at-a-time gate spans both.
/// </summary>
public sealed class AdDiscoveryRunner(IServiceScopeFactory scopeFactory, IHostApplicationLifetime lifetime, ILogger<AdDiscoveryRunner> logger)
{
    public const string ScheduleTrigger = "Schedule";

    private int _running;

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>Starts a run in the background and returns at once; false if one is already going.</summary>
    public bool TryStart(string triggeredBy)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunCoreAsync(triggeredBy, lifetime.ApplicationStopping);
            }
            finally
            {
                Volatile.Write(ref _running, 0);
            }
        });
        return true;
    }

    /// <summary>The scheduled run. Skips quietly if a Run now is going.</summary>
    public async Task RunScheduledAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return;
        try
        {
            await RunCoreAsync(ScheduleTrigger, ct);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task RunCoreAsync(string triggeredBy, CancellationToken ct)
    {
        int? runKey = null;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var runs = scope.ServiceProvider.GetRequiredService<AdDiscoveryRunRepository>();
            runKey = await runs.StartAsync(triggeredBy);
            var summary = await scope.ServiceProvider.GetRequiredService<AdAccountDiscoveryService>().RunAsync(ct);
            var message = summary.SkippedReason ?? (summary.Problems.Count == 0 ? null : string.Join(" ", summary.Problems));
            await runs.FinishAsync(runKey.Value, summary.Status, summary, message);
            logger.LogInformation("AD Account Discovery ({TriggeredBy}): {Status}, {Accounts} accounts from {Read} of {Domains} domains.",
                triggeredBy, summary.Status, summary.AccountsFound, summary.DomainsRead, summary.DomainCount);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AD Account Discovery ({TriggeredBy}) failed unexpectedly.", triggeredBy);
            if (runKey is { } key)
            {
                try
                {
                    using var scope = scopeFactory.CreateScope();
                    var message = ex is OperationCanceledException ? "The run stopped when the application shut down." : $"The run failed: {ex.Message}";
                    await scope.ServiceProvider.GetRequiredService<AdDiscoveryRunRepository>().FinishAsync(key, "Failed", null, message);
                }
                catch (Exception recordEx)
                {
                    logger.LogError(recordEx, "Couldn't record the end of AD Account Discovery run {RunKey}.", key);
                }
            }
        }
    }
}
