using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// D-185: delete and undelete accounts in BlueTrack (47_BlueTrack_AccountDeletion.sql).
/// A BlueTrack delete is a web.account_deletion row; fact_account.IsDeleted
/// (what every list and report filters on) is set alongside it, and the
/// nightly load keeps it set. Every delete and undelete is appended to
/// web.account_deletion_history with its reason.
/// </summary>
public sealed class AccountDeletionRepository(IDbConnectionFactory connectionFactory)
{
    public sealed record DeletionState(long AccountKey, string AccountName, bool DeletedInBlueTrack, bool IsDeletedInSource);

    public async Task<DeletionState?> GetStateAsync(long accountKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<DeletionState>("""
            SELECT fa.AccountKey, fa.AccountName,
                   CAST(CASE WHEN ad.AccountKey IS NULL THEN 0 ELSE 1 END AS BIT) AS DeletedInBlueTrack,
                   fa.IsDeletedInSource
            FROM dbo.fact_account fa
            LEFT JOIN web.account_deletion ad ON ad.AccountKey = fa.AccountKey
            WHERE fa.AccountKey = @accountKey
            """, new { accountKey });
    }

    public async Task DeleteAsync(long accountKey, string reason, int userKey, string? batchId)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("""
            INSERT INTO web.account_deletion (AccountKey, DeletedBy, Reason) VALUES (@accountKey, @userKey, @reason);
            INSERT INTO web.account_deletion_history (AccountKey, Action, Reason, PerformedBy, BatchId)
            VALUES (@accountKey, 'Delete', @reason, @userKey, @batchId);
            UPDATE dbo.fact_account SET IsDeleted = 1 WHERE AccountKey = @accountKey;
            """, new { accountKey, reason, userKey, batchId }, transaction);
        transaction.Commit();
    }

    /// <summary>Removes the BlueTrack delete; the account is then deleted only if CyberArk says so.</summary>
    public async Task UndeleteAsync(long accountKey, string reason, int userKey, string? batchId)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("""
            DELETE FROM web.account_deletion WHERE AccountKey = @accountKey;
            INSERT INTO web.account_deletion_history (AccountKey, Action, Reason, PerformedBy, BatchId)
            VALUES (@accountKey, 'Undelete', @reason, @userKey, @batchId);
            UPDATE dbo.fact_account SET IsDeleted = IsDeletedInSource WHERE AccountKey = @accountKey;
            """, new { accountKey, reason, userKey, batchId }, transaction);
        transaction.Commit();
    }

    public async Task<IReadOnlyList<AccountDeletionHistoryEntry>> GetHistoryAsync(long accountKey)
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<AccountDeletionHistoryEntry>("""
            SELECT h.Action, h.Reason, u.DisplayName AS PerformedByName, h.PerformedAt, h.BatchId
            FROM web.account_deletion_history h
            LEFT JOIN web.app_user u ON u.UserKey = h.PerformedBy
            WHERE h.AccountKey = @accountKey
            ORDER BY h.PerformedAt DESC, h.HistoryKey DESC
            """, new { accountKey })).AsList();
    }
}
