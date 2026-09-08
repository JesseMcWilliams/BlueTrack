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

    /// <summary>
    /// D-117: the Deployment page's "Backup App" button. BACKUP DATABASE's
    /// own T-SQL syntax needs a literal database identifier (no variable/
    /// parameter allowed there), so the current database's real name is read
    /// via DB_NAME() first and bracket-quoted into the command text -- safe
    /// here since it comes from SQL Server itself, not user input; the
    /// destination file path (user-supplied, web.app_config.BackupFolder) is
    /// still passed as a real parameter. No WITH COMPRESSION -- not every
    /// SQL Server edition supports it, and this app doesn't know which
    /// edition it's running against.
    /// </summary>
    public async Task<string> TriggerBackupAsync(string backupFolder)
    {
        using var connection = connectionFactory.Create();
        connection.Open();

        var databaseName = await connection.QuerySingleAsync<string>("SELECT DB_NAME()");
        var fileName = $"{databaseName}_{DateTime.UtcNow:yyyyMMdd_HHmmss}.bak";
        var fullPath = Path.Combine(backupFolder, fileName);
        var quotedDatabaseName = "[" + databaseName.Replace("]", "]]") + "]";

        await connection.ExecuteAsync(
            $"BACKUP DATABASE {quotedDatabaseName} TO DISK = @FullPath WITH INIT",
            new { FullPath = fullPath },
            commandTimeout: 600);

        return fullPath;
    }
}
