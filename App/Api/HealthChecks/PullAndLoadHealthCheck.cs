using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.HealthChecks;

/// <summary>
/// D-348: Unhealthy while a pull or a step of the Import and Load has stopped moving (RunStallDetector); Degraded when
/// the latest pull, Import or Load failed; otherwise Healthy. Shown on the Deployment page, so Tools/Test-BlueTrackHealth.ps1
/// reports it too.
/// </summary>
public sealed class PullAndLoadHealthCheck(RunStallDetector detector, IDbConnectionFactory connectionFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var report = await detector.DetectAsync();
        if (report.Stalls.Count > 0)
            return HealthCheckResult.Unhealthy(string.Join(" ", report.Stalls.Select(s => $"{s.What} has not moved for {s.Minutes} minutes.")));

        using var connection = connectionFactory.Create();
        var failed = (await connection.QueryAsync<string>("""
            SELECT N'The latest Self-Hosted Direct pull ' + LOWER(Status) + N'.'
            FROM (SELECT TOP (1) Status FROM dbo.cyberark_direct_pull ORDER BY PullKey DESC) p
            WHERE p.Status IN (N'Failed', N'Rejected')
            UNION ALL
            SELECT N'The latest ' + r.StepName + N' failed.'
            FROM (SELECT StepName, Status, ROW_NUMBER() OVER (PARTITION BY StepName ORDER BY EtlRunKey DESC) AS n
                  FROM dbo.etl_run_log WITH (NOLOCK) WHERE StepName IN (N'Import', N'Load')) r
            WHERE r.n = 1 AND r.Status = N'Failed'
            """)).ToList();
        return failed.Count > 0
            ? HealthCheckResult.Degraded(string.Join(" ", failed) + " See Admin > Data Sources.")
            : HealthCheckResult.Healthy($"Nothing has stopped moving for {report.StallMinutes} minutes, and the latest pull and load didn't fail.");
    }
}
