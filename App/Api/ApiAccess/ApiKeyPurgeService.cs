using BlueTrack.Api.Audit;

namespace BlueTrack.Api.ApiAccess;

/// <summary>
/// D-243: deletes API keys that expired more than ApiKeyExpiredRetentionDays ago (Global Application
/// Configuration, a year by default; 0 keeps them for ever), so the list doesn't fill with dead keys. Runs a
/// minute after the site starts and then every six hours. Each deletion is in the Audit Log as ApiKeyChanged,
/// attributed to the "BlueTrack API access (system)" user. A key that is merely disabled is never deleted
/// here, only one that has expired.
/// </summary>
public sealed class ApiKeyPurgeService(
    IServiceScopeFactory scopeFactory,
    TimeProvider timeProvider,
    ILogger<ApiKeyPurgeService> logger) : BackgroundService
{
    public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartDelay, timeProvider, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Deleting expired API keys failed -- will try again later.");
                }
                await Task.Delay(Interval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    /// <summary>One pass; returns how many keys it deleted. Public for tests.</summary>
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var keys = scope.ServiceProvider.GetRequiredService<ApiKeyRepository>();
        var audit = scope.ServiceProvider.GetRequiredService<AuditLogger>();

        var days = await keys.GetExpiredRetentionDaysAsync();
        if (days <= 0) return 0;
        var systemUser = await keys.GetSystemUserKeyAsync();
        if (systemUser is null) return 0;

        var cutoff = timeProvider.GetUtcNow().UtcDateTime.AddDays(-days);
        var deleted = 0;
        foreach (var key in await keys.ListExpiredBeforeAsync(cutoff))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!await keys.DeleteAsync(key.ApiKeyKey)) continue;
            await audit.LogAsync("ApiKeyChanged", systemUser.Value, "api_key", key.ApiKeyKey.ToString(),
                detail: $"API key '{key.DisplayName}' ({key.KeyPrefix}) deleted automatically: it expired on {key.ExpiresAt:yyyy-MM-dd}, more than {days} days ago");
            deleted++;
        }
        return deleted;
    }
}
