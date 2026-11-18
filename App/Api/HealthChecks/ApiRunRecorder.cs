using BlueTrack.Api.Controllers;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.HealthChecks;

/// <summary>
/// D-361: records each start and clean stop of the API in web.api_run, so the Deployment page can show when the API,
/// and so everything it schedules, wasn't running. Under IIS a start is the worker process loading the app (after an
/// install, a recycle or a preload); a stop is IIS shutting it down. A failure to record is logged and never stops the
/// API starting. A Singleton, so <see cref="CurrentRunKey"/> is this process's row.
/// </summary>
public sealed class ApiRunRecorder(IDbConnectionFactory connectionFactory, ILogger<ApiRunRecorder> logger) : IHostedService
{
    private long _runKey;

    /// <summary>This process's web.api_run row; null until the start is recorded, or if it couldn't be.</summary>
    public long? CurrentRunKey => Interlocked.Read(ref _runKey) is var key && key > 0 ? key : null;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var key = await new ApiRunRepository(connectionFactory).RecordStartAsync(
                Environment.MachineName, Environment.ProcessId, AboutController.ProductVersion());
            Interlocked.Exchange(ref _runKey, key);
            logger.LogInformation("BlueTrack API started (run {ApiRunKey}, process {ProcessId}).", key, Environment.ProcessId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't record the API's start in web.api_run.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (CurrentRunKey is not { } key) return;
        try
        {
            await new ApiRunRepository(connectionFactory).RecordStopAsync(key);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Couldn't record the API's stop in web.api_run.");
        }
    }
}
