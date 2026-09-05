using Dapper;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.HealthChecks;

/// <summary>
/// D-96 Part 3.2: a real, trivial query through the existing
/// IDbConnectionFactory -- a genuine connectivity check, not a stub.
/// </summary>
public sealed class SqlServerHealthCheck(IDbConnectionFactory connectionFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            using var connection = connectionFactory.Create();
            await connection.QuerySingleAsync<int>(new CommandDefinition("SELECT 1", cancellationToken: cancellationToken));
            return HealthCheckResult.Healthy("SQL Server connection succeeded.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("SQL Server connection failed.", ex);
        }
    }
}
