using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-193 (06_BlueTrack_Baseline_WebLogic.sql):
/// usp_DeriveAccountTargetMap_FromAddress links each active account to every
/// Target whose identifier equals its Address (SourceMethod 'AddressDerived'),
/// keeps those links in step with the data, never touches links made any other
/// way, and marks changed accounts' risk scores stale.
/// </summary>
public class AddressDerivedLinkTests
{
    private static async Task<long> InsertAccountAsync(SqlConnection connection, string address) =>
        await connection.QuerySingleAsync<long>("""
            INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, Address, IsDeleted)
            OUTPUT inserted.AccountKey
            VALUES ((SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'DISCOVERY'), @Id, 'IntegrationTest address link', @Address, 0)
            """, new { Id = $"IntegrationTest_{Guid.NewGuid():N}", Address = address });

    private static async Task<int> InsertTargetAsync(SqlConnection connection, string identifierValue, string identifierType = "Hostname")
    {
        var key = await connection.QuerySingleAsync<int>("""
            INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore)
            OUTPUT inserted.TargetKey
            VALUES ((SELECT TargetTypeKey FROM web.dim_target_type WHERE TypeCode = 'Server'), @Name, 500)
            """, new { Name = $"IntegrationTest_{Guid.NewGuid():N}" });
        await connection.ExecuteAsync("INSERT INTO web.target_identifier (TargetKey, IdentifierType, IdentifierValue) VALUES (@key, @identifierType, @identifierValue)",
            new { key, identifierType, identifierValue });
        return key;
    }

    private static Task<IEnumerable<(int TargetKey, string SourceMethod)>> LinksAsync(SqlConnection connection, long accountKey) =>
        connection.QueryAsync<(int, string)>("SELECT TargetKey, SourceMethod FROM web.account_target_map WHERE AccountKey = @accountKey", new { accountKey });

    private static async Task CleanupAsync(SqlConnection connection, IEnumerable<long> accountKeys, IEnumerable<int> targetKeys)
    {
        foreach (var accountKey in accountKeys)
        {
            await connection.ExecuteAsync("""
                DELETE FROM web.account_target_map WHERE AccountKey = @accountKey;
                DELETE FROM web.account_risk_score WHERE AccountKey = @accountKey;
                DELETE FROM fact_account WHERE AccountKey = @accountKey;
                """, new { accountKey });
        }
        foreach (var targetKey in targetKeys)
        {
            await connection.ExecuteAsync("""
                DELETE FROM web.account_target_map WHERE TargetKey = @targetKey;
                DELETE FROM web.target_identifier WHERE TargetKey = @targetKey;
                DELETE FROM web.dim_target WHERE TargetKey = @targetKey;
                """, new { targetKey });
        }
    }

    [Fact]
    public async Task LinksAccountsToEveryMatchingTarget_AndMarksThemStale()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var address = $"integrationtest-link-{Guid.NewGuid():N}";
        // Two accounts on the same host, and two Targets carrying that name (as a Hostname and as an FQDN identifier).
        var accounts = new[] { await InsertAccountAsync(connection, $"  {address} "), await InsertAccountAsync(connection, address) };
        var targets = new[] { await InsertTargetAsync(connection, address), await InsertTargetAsync(connection, address, "FQDN") };

        try
        {
            await connection.ExecuteAsync("EXEC usp_DeriveAccountTargetMap_FromAddress");

            foreach (var account in accounts)
            {
                var links = (await LinksAsync(connection, account)).ToList();
                Assert.Equal(targets.OrderBy(t => t), links.Select(l => l.TargetKey).OrderBy(t => t));
                Assert.All(links, l => Assert.Equal("AddressDerived", l.SourceMethod));
                Assert.True(await connection.QuerySingleAsync<bool>("SELECT IsRiskScoreStale FROM web.account_risk_score WHERE AccountKey = @account", new { account }));
            }

            // Running again changes nothing.
            await connection.ExecuteAsync("EXEC usp_DeriveAccountTargetMap_FromAddress");
            Assert.Equal(2, (await LinksAsync(connection, accounts[0])).Count());
        }
        finally
        {
            await CleanupAsync(connection, accounts, targets);
        }
    }

    [Fact]
    public async Task RemovesLinksThatNoLongerMatch_ButNeverOtherKindsOfLink()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var address = $"integrationtest-link-{Guid.NewGuid():N}";
        var moved = await InsertAccountAsync(connection, address);
        var deleted = await InsertAccountAsync(connection, address);
        var target = await InsertTargetAsync(connection, address);
        var otherTarget = await InsertTargetAsync(connection, $"integrationtest-other-{Guid.NewGuid():N}");

        try
        {
            await connection.ExecuteAsync("EXEC usp_DeriveAccountTargetMap_FromAddress");
            // A manual link the procedure must leave alone, even though it doesn't match the address.
            await connection.ExecuteAsync("INSERT INTO web.account_target_map (AccountKey, TargetKey, SourceMethod, LoadTimestamp) VALUES (@moved, @otherTarget, 'ManualImport', SYSUTCDATETIME())",
                new { moved, otherTarget });

            await connection.ExecuteAsync("""
                UPDATE fact_account SET Address = 'integrationtest-elsewhere' WHERE AccountKey = @moved;
                UPDATE fact_account SET IsDeleted = 1 WHERE AccountKey = @deleted;
                UPDATE web.account_risk_score SET IsRiskScoreStale = 0 WHERE AccountKey IN (@moved, @deleted);
                EXEC usp_DeriveAccountTargetMap_FromAddress;
                """, new { moved, deleted });

            var movedLinks = (await LinksAsync(connection, moved)).ToList();
            Assert.Equal([(otherTarget, "ManualImport")], movedLinks);
            Assert.Empty(await LinksAsync(connection, deleted));
            Assert.True(await connection.QuerySingleAsync<bool>("SELECT IsRiskScoreStale FROM web.account_risk_score WHERE AccountKey = @moved", new { moved }));
        }
        finally
        {
            await CleanupAsync(connection, [moved, deleted], [target, otherTarget]);
        }
    }
}
