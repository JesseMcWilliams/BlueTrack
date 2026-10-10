using Dapper;

namespace BlueTrack.Api.Data;

/// <summary>D-200: web.backup_run -- one row per database backup, scheduled or manual.</summary>
public sealed class BackupRunRepository(IDbConnectionFactory connectionFactory)
{
    public sealed class BackupRun
    {
        public long BackupRunKey { get; init; }
        public required string TriggerType { get; init; }
        public int? TriggeredBy { get; init; }
        public string? TriggeredByName { get; init; }
        public DateTime StartedAt { get; init; }
        public DateTime? FinishedAt { get; init; }
        public required string Outcome { get; init; }
        public string? FilePath { get; init; }
        public int? FilesDeleted { get; init; }
        public string? ErrorMessage { get; init; }
    }

    public async Task RecordAsync(BackupRun run)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("""
            INSERT INTO web.backup_run (TriggerType, TriggeredBy, StartedAt, FinishedAt, Outcome, FilePath, FilesDeleted, ErrorMessage)
            VALUES (@TriggerType, @TriggeredBy, @StartedAt, @FinishedAt, @Outcome, @FilePath, @FilesDeleted, @ErrorMessage)
            """, run);
    }

    /// <summary>The most recent backups, newest first, for the Deployment page.</summary>
    public async Task<IReadOnlyList<BackupRun>> GetRecentAsync(int count)
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<BackupRun>("""
            SELECT TOP (@count) br.BackupRunKey, br.TriggerType, br.TriggeredBy, au.DisplayName AS TriggeredByName,
                   br.StartedAt, br.FinishedAt, br.Outcome, br.FilePath, br.FilesDeleted, br.ErrorMessage
            FROM web.backup_run br
            LEFT JOIN web.app_user au ON au.UserKey = br.TriggeredBy
            ORDER BY br.StartedAt DESC, br.BackupRunKey DESC
            """, new { count });
        return rows.ToList();
    }
}
