using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// Design_Risk_Scoring.md, D-101-105, D-119 Phase C:
/// usp_DeriveAccountTargetMap_FromPendingSafes -- the one of three direct
/// Account->Target mechanisms needing no external file, reusing the same
/// SafeName LIKE '%[_]Pending%' match D-91's usp_Load_AccountProgressAutoAdvance
/// already uses. TestSafe01_Pending (SafeKey 2) is this project's own
/// existing shared test fixture -- a new synthetic account is added under
/// it rather than reusing TestAccount02 (also in that safe), since other
/// tests may depend on TestAccount02's own existing shape.
/// </summary>
public class PendingSafeDerivedTargetMappingTests
{
    private const int PendingSafeKey = 2; // TestSafe01_Pending

    [Fact]
    public async Task DerivesAccountTargetMap_ForAccountInPendingSafe_WithMatchingAddress()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        var hostname = $"integrationtest-{Guid.NewGuid():N}";
        var accountKey = await connection.QuerySingleAsync<long>("""
            INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, Address, SafeKey, IsDeleted)
            OUTPUT inserted.AccountKey
            VALUES (3, @SourceAccountId, @AccountName, @Address, @SafeKey, 0)
            """, new { SourceAccountId = $"IntegrationTest_{Guid.NewGuid():N}", AccountName = "IntegrationTest PendingSafe Account", Address = hostname, SafeKey = PendingSafeKey });

        var targetKey = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_target (TargetType, TargetName, RiskScore) OUTPUT inserted.TargetKey VALUES ('Server', @TargetName, 700)",
            new { TargetName = $"IntegrationTest_{Guid.NewGuid():N}" });
        await connection.ExecuteAsync(
            "INSERT INTO web.target_identifier (TargetKey, IdentifierType, IdentifierValue) VALUES (@TargetKey, 'Hostname', @Hostname)",
            new { TargetKey = targetKey, Hostname = hostname });

        try
        {
            await connection.ExecuteAsync("EXEC usp_DeriveAccountTargetMap_FromPendingSafes");

            var mapping = await connection.QuerySingleOrDefaultAsync<(long AccountKey, int TargetKey, string SourceMethod)?>(
                "SELECT AccountKey, TargetKey, SourceMethod FROM web.account_target_map WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            Assert.NotNull(mapping);
            Assert.Equal(targetKey, mapping!.Value.TargetKey);
            Assert.Equal("PendingSafeDerived", mapping.Value.SourceMethod);

            var riskScoreRow = await connection.QuerySingleOrDefaultAsync<bool?>(
                "SELECT IsRiskScoreStale FROM web.account_risk_score WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            Assert.True(riskScoreRow); // lazily created, marked stale

            // Idempotent -- running it again doesn't duplicate the mapping row.
            await connection.ExecuteAsync("EXEC usp_DeriveAccountTargetMap_FromPendingSafes");
            var countAfterSecondRun = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM web.account_target_map WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            Assert.Equal(1, countAfterSecondRun);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.account_target_map WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM web.account_risk_score WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM web.target_identifier WHERE TargetKey = @TargetKey", new { TargetKey = targetKey });
            await connection.ExecuteAsync("DELETE FROM web.dim_target WHERE TargetKey = @TargetKey", new { TargetKey = targetKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
        }
    }

    [Fact]
    public async Task DoesNotCreateMapping_ForAccountWithNoMatchingTargetIdentifier()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        var unmatchedAddress = $"integrationtest-nomatch-{Guid.NewGuid():N}";
        var accountKey = await connection.QuerySingleAsync<long>("""
            INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, Address, SafeKey, IsDeleted)
            OUTPUT inserted.AccountKey
            VALUES (3, @SourceAccountId, @AccountName, @Address, @SafeKey, 0)
            """, new { SourceAccountId = $"IntegrationTest_{Guid.NewGuid():N}", AccountName = "IntegrationTest No-Match Account", Address = unmatchedAddress, SafeKey = PendingSafeKey });

        try
        {
            await connection.ExecuteAsync("EXEC usp_DeriveAccountTargetMap_FromPendingSafes");

            var count = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM web.account_target_map WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            Assert.Equal(0, count); // no matching Target -- correctly left unmapped, not auto-created

            var riskScoreRow = await connection.QuerySingleOrDefaultAsync<bool?>(
                "SELECT IsRiskScoreStale FROM web.account_risk_score WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            Assert.True(riskScoreRow); // still gets a stale row -- conservative, regardless of whether a new mapping resulted
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.account_risk_score WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
        }
    }
}
