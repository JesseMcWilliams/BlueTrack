using System.Text.Json;
using Dapper;

namespace BlueTrack.Api.Data;

/// <summary>
/// D-212 (Data Sources phase 4a): reads and writes one feed source's accounts
/// in dbo.fact_account / fact_account_progress for AccountInventoryImportService.
/// The whole import is applied in one transaction.
/// </summary>
public sealed class AccountInventoryRepository(IDbConnectionFactory connectionFactory)
{
    public sealed class ExistingAccount
    {
        public long AccountKey { get; init; }
        public required string SourceAccountId { get; init; }
        public required string AccountName { get; init; }
        public string? Address { get; init; }
        public string? UserName { get; init; }
        public int? PlatformKey { get; init; }
        public bool IsDeletedInSource { get; init; }
    }

    public sealed class Platform
    {
        public int PlatformKey { get; init; }
        public required string PlatformID { get; init; }
        public string? PlatformName { get; init; }
        public required string SourceSystemCode { get; init; }
    }

    public sealed record NewAccount(string SourceAccountId, string AccountName, string Address, string UserName, int? PlatformKey, string? OwnerName, string? Notes);
    public sealed record ChangedAccount(long AccountKey, string AccountName, string Address, string UserName, int? PlatformKey, bool Restore);

    public async Task<IReadOnlyList<ExistingAccount>> GetAccountsAsync(int sourceSystemKey)
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<ExistingAccount>("""
            SELECT AccountKey, SourceAccountId, AccountName, Address, UserName, PlatformKey, IsDeletedInSource
            FROM dbo.fact_account WHERE SourceSystemKey = @sourceSystemKey
            """, new { sourceSystemKey })).ToList();
    }

    /// <summary>CyberArk platforms, for a row's optional PlatformName.</summary>
    public async Task<IReadOnlyList<Platform>> GetPlatformsAsync()
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<Platform>("""
            SELECT p.PlatformKey, p.PlatformID, p.PlatformName, ss.SourceSystemCode
            FROM dbo.dim_platform p JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = p.SourceSystemKey
            """)).ToList();
    }

    /// <summary>
    /// Creates, updates (restoring an account that was missing from an earlier
    /// file) and marks deleted, in one transaction. A new account gets a
    /// Discovered / Not Started progress row, as usp_Load_FactAccountProgress
    /// gives a new CyberArk account. IsDeleted follows D-185: deleted in the
    /// source, or deleted in BlueTrack (web.account_deletion).
    /// </summary>
    public async Task ApplyAsync(int sourceSystemKey, IReadOnlyList<NewAccount> creates, IReadOnlyList<ChangedAccount> updates, IReadOnlyList<long> deletes)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        foreach (var a in creates)
        {
            var accountKey = await connection.QuerySingleAsync<long>("""
                INSERT INTO dbo.fact_account (SourceSystemKey, SourceAccountId, AccountName, Address, UserName, PlatformKey, IsDeleted, IsDeletedInSource, CreatedDate)
                OUTPUT inserted.AccountKey
                VALUES (@sourceSystemKey, @SourceAccountId, @AccountName, @Address, @UserName, @PlatformKey, 0, 0, CAST(SYSDATETIME() AS DATE))
                """, new { sourceSystemKey, a.SourceAccountId, a.AccountName, a.Address, a.UserName, a.PlatformKey }, transaction);
            await InsertProgressAsync(connection, transaction, accountKey, a.OwnerName, a.Notes);
        }

        foreach (var a in updates)
        {
            await connection.ExecuteAsync("""
                UPDATE fa SET AccountName = @AccountName, Address = @Address, UserName = @UserName, PlatformKey = @PlatformKey,
                    IsDeletedInSource = 0,
                    IsDeleted = CASE WHEN EXISTS (SELECT 1 FROM web.account_deletion ad WHERE ad.AccountKey = fa.AccountKey) THEN 1 ELSE 0 END
                FROM dbo.fact_account fa WHERE fa.AccountKey = @AccountKey
                """, new { a.AccountKey, a.AccountName, a.Address, a.UserName, a.PlatformKey }, transaction);
            if (a.Restore)
            {
                await InsertProgressAsync(connection, transaction, a.AccountKey, null, null);
            }
        }

        if (deletes.Count > 0)
        {
            await connection.ExecuteAsync("""
                UPDATE dbo.fact_account SET IsDeletedInSource = 1, IsDeleted = 1
                WHERE SourceSystemKey = @sourceSystemKey AND AccountKey IN (SELECT CAST(value AS BIGINT) FROM OPENJSON(@keys))
                """, new { sourceSystemKey, keys = JsonSerializer.Serialize(deletes) }, transaction);
        }

        transaction.Commit();
    }

    /// <summary>
    /// D-212 (phase 4b): dbo.usp_Link_FeedAccounts for this source -- links each
    /// feed account CyberArk also has (same user name and address) and moves it
    /// from Discovered to Onboarded to Vault, audit-logged as <paramref name="userKey"/>.
    /// </summary>
    public async Task<(int Linked, int Moved)> LinkAsync(int sourceSystemKey, int userKey)
    {
        using var connection = connectionFactory.Create();
        var counts = await connection.QuerySingleAsync<(int LinkedCount, int MovedCount)>(
            "dbo.usp_Link_FeedAccounts", new { PerformedByUserKey = userKey, SourceSystemKey = sourceSystemKey },
            commandType: System.Data.CommandType.StoredProcedure);
        return (counts.LinkedCount, counts.MovedCount);
    }

    // Only when the account has none (a restored account keeps its old row).
    private static Task InsertProgressAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, long accountKey, string? ownerName, string? notes) =>
        connection.ExecuteAsync("""
            INSERT INTO dbo.fact_account_progress (AccountKey, CurrentStageKey, CurrentStatusKey, AccountTypeKey, SORKey, OwnerName, Notes)
            SELECT fa.AccountKey,
                   dbo.fn_StartStageKey(),   -- D-284
                   (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = 'Not Started'),
                   pm.AccountTypeKey, pm.SORKey, @ownerName, @notes
            FROM dbo.fact_account fa
            LEFT JOIN dbo.platform_account_type_map pm ON pm.PlatformKey = fa.PlatformKey
            WHERE fa.AccountKey = @accountKey
              AND NOT EXISTS (SELECT 1 FROM dbo.fact_account_progress p WHERE p.AccountKey = fa.AccountKey)
            """, new { accountKey, ownerName, notes }, transaction);
}
