using Dapper;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.AdDiscovery;

/// <summary>D-317 (59_BlueTrack_AdDiscoveryRuns.sql): AD Account Discovery's run time and its run log.</summary>
public sealed class AdDiscoveryRunRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<string> GetRunTimeAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<string>("SELECT TOP (1) CONVERT(varchar(5), AdDiscoveryRunTime, 108) FROM web.app_config");
    }

    /// <param name="runTime">HH:mm, already checked.</param>
    public async Task SetRunTimeAsync(string runTime)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("UPDATE web.app_config SET AdDiscoveryRunTime = CAST(@runTime AS time(0))", new { runTime });
    }

    public async Task<int> StartAsync(string triggeredBy)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>(
            "INSERT INTO web.ad_discovery_run (TriggeredBy) OUTPUT inserted.AdDiscoveryRunKey VALUES (@triggeredBy)", new { triggeredBy });
    }

    public async Task FinishAsync(int runKey, string status, AdDiscoveryRunSummary? summary, string? message)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("""
            UPDATE web.ad_discovery_run
            SET FinishedAt = SYSUTCDATETIME(), Status = @status, DomainCount = @DomainCount, DomainsRead = @DomainsRead,
                AccountsFound = @AccountsFound, Message = LEFT(@message, 2000)
            WHERE AdDiscoveryRunKey = @runKey
            """, new { runKey, status, summary?.DomainCount, summary?.DomainsRead, summary?.AccountsFound, message });
    }

    /// <summary>A run still marked Running when the app starts was cut off by a restart; it says so.</summary>
    public async Task CloseInterruptedAsync()
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("""
            UPDATE web.ad_discovery_run SET FinishedAt = SYSUTCDATETIME(), Status = 'Failed', Message = N'The run stopped when the application restarted.'
            WHERE Status = 'Running'
            """);
    }

    public async Task<IReadOnlyList<AdDiscoveryRun>> GetRecentAsync(int count)
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<AdDiscoveryRun>("""
            SELECT TOP (@count) AdDiscoveryRunKey, TriggeredBy, StartedAt, FinishedAt, Status, DomainCount, DomainsRead, AccountsFound, Message
            FROM web.ad_discovery_run ORDER BY AdDiscoveryRunKey DESC
            """, new { count })).AsList();
    }

    public async Task<int> PurgeAsync(int retentionDays)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync(
            "DELETE FROM web.ad_discovery_run WHERE StartedAt < DATEADD(DAY, -@retentionDays, SYSUTCDATETIME()) AND Status <> 'Running'", new { retentionDays });
    }

    /// <summary>D-324: every LDAP domain, with whether discovery reads it (enabled and used as a data source).</summary>
    public async Task<IReadOnlyList<AdDiscoveryDomain>> GetDomainsAsync()
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<AdDiscoveryDomain>(
            "SELECT LdapConfigKey, DomainName, IsEnabled, UseAsDataSource FROM web.ldap_config ORDER BY DomainName")).AsList();
    }
}

/// <summary>D-324: an LDAP domain as Data Sources lists it.</summary>
public sealed class AdDiscoveryDomain
{
    public int LdapConfigKey { get; init; }
    public required string DomainName { get; init; }
    public bool IsEnabled { get; init; }
    public bool UseAsDataSource { get; init; }
}

/// <summary>D-317: one row of the AD Account Discovery run log, as Data Sources shows it.</summary>
public sealed class AdDiscoveryRun
{
    public int AdDiscoveryRunKey { get; init; }
    public required string TriggeredBy { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public required string Status { get; init; }
    public int? DomainCount { get; init; }
    public int? DomainsRead { get; init; }
    public int? AccountsFound { get; init; }
    public string? Message { get; init; }
}
