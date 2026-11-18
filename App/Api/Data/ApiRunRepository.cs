using Dapper;

namespace BlueTrack.Api.Data;

/// <summary>D-361: web.api_run -- one row each time the API starts, closed when it stops cleanly.</summary>
public sealed class ApiRunRepository(IDbConnectionFactory connectionFactory)
{
    public const int KeepDays = 180;

    public sealed class ApiRun
    {
        public long ApiRunKey { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? StoppedAt { get; init; }
        public string MachineName { get; init; } = "";
        public int ProcessId { get; init; }
        public string Version { get; init; } = "";
        /// <summary>D-362: when this run's pull scheduler last woke and checked whether the pull was due.</summary>
        public DateTime? PullSchedulerCheckedAt { get; init; }
        /// <summary>The previous run's StoppedAt (or StartedAt, if it never stopped cleanly): the API wasn't running between it and StartedAt.</summary>
        public DateTime? PreviousStoppedAt { get; init; }
        public bool PreviousStoppedCleanly { get; init; }
    }

    /// <summary>Records a start and deletes rows older than KeepDays; returns the new row's key.</summary>
    public async Task<long> RecordStartAsync(string machineName, int processId, string version)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteScalarAsync<long>("""
            DELETE FROM web.api_run WHERE StartedAt < DATEADD(DAY, -@KeepDays, SYSUTCDATETIME());
            INSERT INTO web.api_run (MachineName, ProcessId, Version) VALUES (@machineName, @processId, @version);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
            """, new { KeepDays, machineName, processId, version });
    }

    /// <summary>D-362: the pull scheduler woke and checked; a single-row update, once a minute.</summary>
    public async Task MarkPullSchedulerCheckedAsync(long apiRunKey)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("UPDATE web.api_run SET PullSchedulerCheckedAt = SYSUTCDATETIME() WHERE ApiRunKey = @apiRunKey", new { apiRunKey });
    }

    /// <summary>D-362: one run (this process's, for Data Sources), or null.</summary>
    public async Task<ApiRun?> GetAsync(long apiRunKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<ApiRun>("""
            SELECT ApiRunKey, StartedAt, StoppedAt, MachineName, ProcessId, Version, PullSchedulerCheckedAt
            FROM web.api_run WHERE ApiRunKey = @apiRunKey
            """, new { apiRunKey });
    }

    public async Task RecordStopAsync(long apiRunKey)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("UPDATE web.api_run SET StoppedAt = SYSUTCDATETIME() WHERE ApiRunKey = @apiRunKey", new { apiRunKey });
    }

    /// <summary>The most recent starts, newest first, each with the end of the run before it.</summary>
    public async Task<IReadOnlyList<ApiRun>> GetRecentAsync(int count)
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<ApiRun>("""
            SELECT TOP (@count) r.ApiRunKey, r.StartedAt, r.StoppedAt, r.MachineName, r.ProcessId, r.Version, r.PullSchedulerCheckedAt,
                   COALESCE(p.StoppedAt, p.StartedAt) AS PreviousStoppedAt,
                   CAST(CASE WHEN p.StoppedAt IS NOT NULL OR p.ApiRunKey IS NULL THEN 1 ELSE 0 END AS BIT) AS PreviousStoppedCleanly
            FROM web.api_run r
            OUTER APPLY (SELECT TOP (1) ApiRunKey, StartedAt, StoppedAt FROM web.api_run WHERE ApiRunKey < r.ApiRunKey ORDER BY ApiRunKey DESC) p
            ORDER BY r.ApiRunKey DESC
            """, new { count });
        return rows.ToList();
    }
}
