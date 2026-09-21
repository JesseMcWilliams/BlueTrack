namespace BlueTrack.Api.AdDiscovery;

/// <summary>
/// AD Account Discovery feature (2026-09-16): runs AdAccountDiscoveryService
/// nightly, following NotificationCheckBackgroundService's exact precedent
/// (this app's only other same-process recurring-work example) -- a
/// Singleton BackgroundService that creates its own DI scope per run (since
/// AdAccountDiscoveryService depends on Scoped repositories) and a plain
/// Task.Delay(1 day) loop rather than a cron-style scheduler, matching this
/// project's stated preference for the simplest mechanism that works over
/// introducing new scheduling infrastructure.
///
/// Per-domain failure isolation already lives inside
/// AdAccountDiscoveryService itself (one unreachable domain doesn't stop the
/// others); the outer try/catch here is a second, coarser safety net so an
/// entirely unexpected failure (e.g. resolving a repository) still can't
/// crash this background service or stop future nightly runs.
/// </summary>
public sealed class AdAccountDiscoveryBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<AdAccountDiscoveryBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan RunInterval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync();

            try
            {
                await Task.Delay(RunInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    private async Task RunOnceAsync()
    {
        using var scope = scopeFactory.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<AdAccountDiscoveryService>();

        try
        {
            await service.RunAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "AD Account Discovery run failed unexpectedly -- will try again next cycle.");
        }
    }
}
