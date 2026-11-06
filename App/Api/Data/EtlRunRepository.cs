using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// D-337: the nightly Import, Load and Snapshot runs (dbo.etl_run_log), the Load's procedures (dbo.etl_run_step) and,
/// for a run still going, what its SQL Server session is doing (dbo.usp_Get_EtlSessionActivity, Database/68).
/// The Load writes its steps inside its own transaction, so they are read WITH (NOLOCK): waiting for that transaction
/// would hang this page exactly when it's needed.
/// </summary>
public sealed class EtlRunRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<EtlRunRow>> GetRecentAsync(int count)
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<EtlRunRow>("""
            SELECT TOP (@count) r.EtlRunKey, r.StepName, r.StartedAt, r.CompletedAt, r.Status, r.ErrorMessage, r.ErrorProcedure, r.ErrorLine, r.StartedBy,
                   (SELECT COUNT(*) FROM dbo.etl_run_step s WITH (NOLOCK) WHERE s.EtlRunKey = r.EtlRunKey) AS StepCount,
                   CASE WHEN r.StepName = 'Import' THEN (SELECT COUNT(*) FROM dbo.import_log il WITH (NOLOCK)
                        WHERE il.StartedAt >= r.StartedAt AND il.StartedAt <= COALESCE(r.CompletedAt, SYSUTCDATETIME())) ELSE 0 END AS FileCount
            FROM dbo.etl_run_log r WITH (NOLOCK)
            ORDER BY r.EtlRunKey DESC
            """, new { count })).ToList();
    }

    public async Task<IReadOnlyList<EtlRunStepRow>> GetStepsAsync(long etlRunKey)
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<EtlRunStepRow>("""
            SELECT StepOrder, ProcName, StartedAt, CompletedAt, Status
            FROM dbo.etl_run_step WITH (NOLOCK)
            WHERE EtlRunKey = @etlRunKey ORDER BY StepOrder
            """, new { etlRunKey })).ToList();
    }

    /// <summary>
    /// D-351: an Import run's files (dbo.import_log, one row per Privilege Cloud export file, written as each starts and
    /// ends), matched to the run by time as the NightlyImportFailed email does; and the Self-Hosted Direct pull it
    /// checked (the latest one to finish before it started). Null when the run isn't an Import.
    /// </summary>
    public async Task<EtlImportDetail?> GetImportDetailAsync(long etlRunKey)
    {
        using var connection = connectionFactory.Create();
        using var grid = await connection.QueryMultipleAsync("""
            DECLARE @start DATETIME2, @end DATETIME2;
            SELECT @start = StartedAt, @end = COALESCE(CompletedAt, SYSUTCDATETIME())
            FROM dbo.etl_run_log WITH (NOLOCK) WHERE EtlRunKey = @etlRunKey AND StepName = 'Import';
            SELECT CAST(CASE WHEN @start IS NULL THEN 0 ELSE 1 END AS BIT);
            SELECT il.SourceFileName, ss.SourceSystemName, il.RowsLoaded, il.StartedAt, il.CompletedAt, il.Status, il.ErrorMessage
            FROM dbo.import_log il WITH (NOLOCK)
            LEFT JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = il.SourceSystemKey
            WHERE @start IS NOT NULL AND il.StartedAt >= @start AND il.StartedAt <= @end
            ORDER BY il.StartedAt;
            SELECT TOP (1) PullKey, CompletedAt, Status
            FROM dbo.cyberark_direct_pull
            WHERE @start IS NOT NULL AND CompletedAt IS NOT NULL AND CompletedAt <= @start
            ORDER BY PullKey DESC;
            """, new { etlRunKey });
        if (!await grid.ReadSingleAsync<bool>()) return null;
        var files = (await grid.ReadAsync<EtlImportFile>()).ToList();
        var pull = await grid.ReadFirstOrDefaultAsync<EtlImportPull>();
        return new EtlImportDetail { Files = files, Pull = pull };
    }

    /// <summary>Each run still Started in the last day, with its session's activity and, for a Load, its current procedure.</summary>
    public async Task<IReadOnlyList<EtlRunningRun>> GetRunningAsync()
    {
        using var connection = connectionFactory.Create();
        var activity = (await connection.QueryAsync<EtlSessionActivity>(
            "dbo.usp_Get_EtlSessionActivity", commandType: System.Data.CommandType.StoredProcedure, commandTimeout: 30)).ToList();
        if (activity.Count == 0) return [];

        var steps = (await connection.QueryAsync<(long EtlRunKey, int StepOrder, string ProcName, DateTime StartedAt, DateTime? CompletedAt, string Status)>("""
            SELECT EtlRunKey, StepOrder, ProcName, StartedAt, CompletedAt, Status
            FROM dbo.etl_run_step WITH (NOLOCK)
            WHERE EtlRunKey IN @keys
            """, new { keys = activity.Select(a => a.EtlRunKey).ToList() })).ToList();

        // D-351: a running Import's latest file still Started.
        var currentFiles = (await connection.QueryAsync<(long EtlRunKey, string SourceFileName)>("""
            SELECT r.EtlRunKey, f.SourceFileName
            FROM dbo.etl_run_log r WITH (NOLOCK)
            CROSS APPLY (SELECT TOP (1) il.SourceFileName FROM dbo.import_log il WITH (NOLOCK)
                         WHERE il.StartedAt >= r.StartedAt AND il.Status = 'Started' ORDER BY il.StartedAt DESC) f
            WHERE r.EtlRunKey IN @keys AND r.StepName = 'Import'
            """, new { keys = activity.Select(a => a.EtlRunKey).ToList() })).ToList();

        return activity.Select(a =>
        {
            var mine = steps.Where(s => s.EtlRunKey == a.EtlRunKey).OrderBy(s => s.StepOrder).ToList();
            var current = mine.LastOrDefault();
            return new EtlRunningRun
            {
                Activity = a,
                CurrentFile = a.StepName == "Import" ? currentFiles.FirstOrDefault(f => f.EtlRunKey == a.EtlRunKey).SourceFileName : null,
                StepsDone = mine.Count(s => s.Status == "Succeeded"),
                CurrentStep = current == default ? null : new EtlRunStepRow
                {
                    StepOrder = current.StepOrder, ProcName = current.ProcName, StartedAt = current.StartedAt,
                    CompletedAt = current.CompletedAt, Status = current.Status
                }
            };
        }).ToList();
    }

    /// <summary>D-348: the stall alert time (5-1440 minutes).</summary>
    public async Task SaveStallMinutesAsync(int minutes)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("UPDATE web.app_config SET RunStallMinutes = @minutes", new { minutes });
    }

    /// <summary>
    /// D-348: stops this database's Import and Load SQL Agent job (sp_stop_job, allowed to the app's login by msdb's
    /// SQLAgentOperatorRole, Database/Manual/08). Throws SqlException when the Agent refuses, such as when the job
    /// isn't running. The Load's transaction is rolled back by SQL Server as the job's session ends.
    /// </summary>
    public async Task StopImportLoadJobAsync()
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("""
            DECLARE @job SYSNAME = N'BlueTrack (' + DB_NAME() + N') - Import and Load';
            EXEC msdb.dbo.sp_stop_job @job_name = @job;
            """);
    }

    /// <summary>
    /// D-348: marks the runs the job was running Failed, since a stopped job never gets to its CATCH. Only rows still
    /// Started; a run that finished meanwhile keeps its outcome.
    /// </summary>
    public async Task<int> MarkCancelledAsync(IReadOnlyList<long> etlRunKeys, string message)
    {
        if (etlRunKeys.Count == 0) return 0;
        using var connection = connectionFactory.Create();
        // A run whose row the stopped session still holds is left for the next look, rather than hanging this request.
        return await connection.ExecuteAsync("""
            SET LOCK_TIMEOUT 15000;
            UPDATE dbo.etl_run_log
            SET Status = 'Failed', CompletedAt = SYSUTCDATETIME(), ErrorMessage = LEFT(@message, 2000)
            WHERE EtlRunKey IN @etlRunKeys AND Status = 'Started'
            """, new { etlRunKeys, message });
    }
}
