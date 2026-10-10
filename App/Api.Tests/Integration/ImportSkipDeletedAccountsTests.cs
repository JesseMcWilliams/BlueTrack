using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-184 (46_BlueTrack_ImportSkipDeletedAccounts.sql): usp_Load_FactAccount
/// never adds an account the CyberArk export marks as deleted, keeps and
/// flags one BlueTrack already has, and reads Self-Hosted CAFDeletionDate
/// only when it's a real date (not an epoch placeholder, not before
/// creation). The procedure flags every Privilege Cloud / Self-Hosted
/// account missing from staging as deleted, so the test saves those flags
/// first and puts them back afterwards; staging is empty on the test
/// database otherwise.
/// </summary>
public class ImportSkipDeletedAccountsTests
{
    private static SqlConnection Open() => new(TestDatabase.ConnectionString);

    [Fact]
    public async Task Load_SkipsDeletedAccounts_FlagsExistingOnes_AndIgnoresPlaceholderDeletionDates()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var batch = Guid.NewGuid();
        await using var connection = Open();
        var saved = (await connection.QueryAsync<(long AccountKey, bool IsDeleted)>("""
            SELECT fa.AccountKey, fa.IsDeleted FROM dbo.fact_account fa
            JOIN dbo.dim_source_system s ON s.SourceSystemKey = fa.SourceSystemKey
            WHERE s.SourceSystemCode IN ('PRIVCLOUD', 'SELFHOSTED')
            """)).ToList();

        // Self-Hosted file IDs: random, so a re-run never collides with a leftover row.
        var safeId = Random.Shared.NextInt64(900_000_000, 999_999_999);
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO stg_pc_accounts (ImportBatchId, SourceFileName, AccountID, AccountName, SafeName, Deleted)
                VALUES (@batch, 'test', @live, @live, 'TestSafe', 0),
                       (@batch, 'test', @gone, @gone, 'TestSafe', 1);
                INSERT INTO stg_sh_files (ImportBatchId, SourceFileName, CAFSafeID, CAFFileID, CAFFileName, CAFType, CAFCreationDate, CAFDeletionDate)
                VALUES (@batch, 'test', @safeId, 1, 'sh-none',  2, '2025-01-01', NULL),
                       (@batch, 'test', @safeId, 2, 'sh-epoch', 2, '2025-01-01', '1970-01-01'),
                       (@batch, 'test', @safeId, 3, 'sh-real',  2, '2025-01-01', '2026-01-01'),
                       (@batch, 'test', @safeId, 4, 'sh-early', 2, '2025-01-01', '2024-06-01');
                EXEC usp_Load_FactAccount;
                """, new { batch, live = $"T{tag}_live", gone = $"T{tag}_gone", safeId });

            async Task<bool?> DeletedFlag(string sourceAccountId) =>
                await connection.QuerySingleOrDefaultAsync<bool?>(
                    "SELECT IsDeleted FROM dbo.fact_account WHERE SourceAccountId = @sourceAccountId", new { sourceAccountId });

            Assert.Equal(false, await DeletedFlag($"T{tag}_live"));
            Assert.Null(await DeletedFlag($"T{tag}_gone"));          // marked deleted in the export: not imported
            Assert.Equal(false, await DeletedFlag($"{safeId}_1"));   // no deletion date
            Assert.Equal(false, await DeletedFlag($"{safeId}_2"));   // epoch placeholder
            Assert.Null(await DeletedFlag($"{safeId}_3"));           // real deletion date: not imported
            Assert.Equal(false, await DeletedFlag($"{safeId}_4"));   // "deleted" before it was created

            // Now the export marks accounts BlueTrack already has: kept, and flagged.
            await connection.ExecuteAsync("""
                UPDATE stg_pc_accounts SET Deleted = 1 WHERE ImportBatchId = @batch AND AccountID = @live;
                UPDATE stg_sh_files SET CAFDeletionDate = '2026-02-01' WHERE ImportBatchId = @batch AND CAFFileID = 1;
                EXEC usp_Load_FactAccount;
                """, new { batch, live = $"T{tag}_live" });
            Assert.Equal(true, await DeletedFlag($"T{tag}_live"));
            Assert.Equal(true, await DeletedFlag($"{safeId}_1"));
            Assert.Equal(false, await DeletedFlag($"{safeId}_2"));
        }
        finally
        {
            await connection.ExecuteAsync("""
                DELETE FROM stg_pc_accounts WHERE ImportBatchId = @batch;
                DELETE FROM stg_sh_files WHERE ImportBatchId = @batch;
                DELETE FROM dbo.fact_account WHERE SourceAccountId LIKE @pcPattern OR SourceAccountId LIKE @shPattern;
                """, new { batch, pcPattern = $"T{tag}[_]%", shPattern = $"{safeId}[_]%" });
            foreach (var (accountKey, isDeleted) in saved)
            {
                await connection.ExecuteAsync("UPDATE dbo.fact_account SET IsDeleted = @isDeleted WHERE AccountKey = @accountKey", new { accountKey, isDeleted });
            }
        }
    }
}
