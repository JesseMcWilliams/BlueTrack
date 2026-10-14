using Microsoft.Extensions.Diagnostics.HealthChecks;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.HealthChecks;

/// <summary>
/// D-234: SIEM forwarding. Healthy when off. When on: Degraded if the last send failed, or if the oldest
/// audit row waiting to be forwarded is older than <see cref="MaxLag"/> (the collector or the forwarder has
/// stopped keeping up); Healthy otherwise.
/// </summary>
public sealed class SiemForwardingHealthCheck(SiemForwardingRepository repository, TimeProvider timeProvider) : IHealthCheck
{
    public static readonly TimeSpan MaxLag = TimeSpan.FromMinutes(15);

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var settings = await repository.GetSettingsAsync();
        if (!settings.IsEnabled) return HealthCheckResult.Healthy("SIEM forwarding is off.");

        var status = await repository.GetStatusAsync();
        var target = $"{settings.Host}:{settings.Port} ({settings.Transport})";
        if (status.LastError is not null)
        {
            return HealthCheckResult.Degraded(
                $"The last send to {target} failed at {status.LastErrorAt:yyyy-MM-dd HH:mm:ss} UTC ({status.ConsecutiveFailures} in a row): {status.LastError}");
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (status.OldestPendingAt is { } oldest && now - oldest > MaxLag)
        {
            return HealthCheckResult.Degraded(
                $"{status.PendingCount} audit events wait to be forwarded to {target}; the oldest is from {oldest:yyyy-MM-dd HH:mm:ss} UTC, more than {MaxLag.TotalMinutes:0} minutes ago.");
        }

        return HealthCheckResult.Healthy(status.PendingCount == 0
            ? $"Forwarding to {target}; nothing waiting."
            : $"Forwarding to {target}; {status.PendingCount} audit events waiting.");
    }
}
