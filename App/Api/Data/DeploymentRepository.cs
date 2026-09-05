using Dapper;
using Microsoft.Data.SqlClient;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the Deployment admin page's SQL Server backup status (D-96/D-97).
/// </summary>
public sealed class DeploymentRepository(IDbConnectionFactory connectionFactory)
{
    /// <summary>
    /// SQL Server's own native backup history -- the user's explicit choice
    /// (D-96) over a specific third-party tool, since msdb.dbo.backupset is
    /// SQL Server's universal backup ledger regardless of which mechanism
    /// (a maintenance plan, Ola Hallengren's scripts, a third-party tool)
    /// actually writes it. database_name = DB_NAME() targets whichever
    /// database the running connection string points at, not hardcoded.
    ///
    /// Per D-97, this app's own SQL account is deliberately least-privileged
    /// (D-30) and almost certainly lacks msdb read access until a DBA grants
    /// it -- a permission-denied SqlException is caught here and turned into
    /// a plain "unavailable" result, not a raw 500.
    /// </summary>
    public async Task<BackupStatusResult> GetBackupStatusAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT type AS BackupType, MAX(backup_finish_date) AS LastBackupFinishDate
            FROM msdb.dbo.backupset
            WHERE database_name = DB_NAME()
            GROUP BY type
            """;

        try
        {
            var rows = await connection.QueryAsync<BackupStatusEntry>(sql);
            return new BackupStatusResult { Available = true, Entries = rows.ToList() };
        }
        catch (SqlException)
        {
            return new BackupStatusResult
            {
                Available = false,
                Error = "Backup history unavailable -- check msdb permissions."
            };
        }
    }
}
