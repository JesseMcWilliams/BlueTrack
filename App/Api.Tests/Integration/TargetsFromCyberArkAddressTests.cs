using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-123: usp_DeriveTargetsFromAccountAddress -- generates web.dim_target/
/// target_identifier rows directly from fact_account.Address, a deliberate
/// reversal of Phase C's "gaps are expected, not auto-filled" principle,
/// confirmed directly with the user. Covers every active account with a
/// non-null Address (not just the narrower _Pending-safe subset Phase C's
/// own procedure covers), classifies Address as IPAddress/Hostname via a
/// real shape check, and stops at creating the Target row -- it never
/// links an account to it (that stays exclusively the job of the existing
/// CSV import / Phase C / manual-link mechanisms).
/// </summary>
public class TargetsFromCyberArkAddressTests
{
    private static async Task<long> InsertAccountAsync(SqlConnection connection, string address) =>
        await connection.QuerySingleAsync<long>("""
            INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, Address, IsDeleted)
            OUTPUT inserted.AccountKey
            VALUES ((SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'DISCOVERY'), @SourceAccountId, @AccountName, @Address, 0)
            """, new { SourceAccountId = $"IntegrationTest_{Guid.NewGuid():N}", AccountName = "IntegrationTest CyberArk Target Account", Address = address });

    private static async Task CleanupAccountAsync(SqlConnection connection, long accountKey)
    {
        await connection.ExecuteAsync("DELETE FROM web.account_risk_score WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
        await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
        await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
        await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
    }

    private static async Task CleanupTargetAsync(SqlConnection connection, string address)
    {
        var targetKeys = (await connection.QueryAsync<int>(
            "SELECT TargetKey FROM web.target_identifier WHERE IdentifierValue = @Address", new { Address = address })).ToList();
        foreach (var targetKey in targetKeys)
        {
            await connection.ExecuteAsync("DELETE FROM web.target_identifier WHERE TargetKey = @TargetKey", new { TargetKey = targetKey });
            await connection.ExecuteAsync("DELETE FROM web.dim_target WHERE TargetKey = @TargetKey", new { TargetKey = targetKey });
        }
    }

    [Fact]
    public async Task CreatesTarget_ForHostnameShapedAddress_ClassifiedAsHostname()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var address = $"integrationtest-host-{Guid.NewGuid():N}";
        var accountKey = await InsertAccountAsync(connection, address);

        try
        {
            await connection.ExecuteAsync("EXEC usp_DeriveTargetsFromAccountAddress");

            var target = await connection.QuerySingleOrDefaultAsync<(int TargetKey, string TargetType, int RiskScore, string DiscoverySource)?>("""
                SELECT dt.TargetKey, dtt.TypeCode AS TargetType, dt.RiskScore, dt.DiscoverySource
                FROM web.dim_target dt
                JOIN web.dim_target_type dtt ON dtt.TargetTypeKey = dt.TargetTypeKey
                JOIN web.target_identifier ti ON ti.TargetKey = dt.TargetKey
                WHERE ti.IdentifierValue = @Address
                """, new { Address = address });

            Assert.NotNull(target);
            Assert.Equal("Other", target!.Value.TargetType);
            Assert.Equal(0, target.Value.RiskScore);
            Assert.Equal("CyberArk ETL", target.Value.DiscoverySource);

            var identifierType = await connection.QuerySingleAsync<string>(
                "SELECT IdentifierType FROM web.target_identifier WHERE IdentifierValue = @Address", new { Address = address });
            Assert.Equal("Hostname", identifierType);
        }
        finally
        {
            await CleanupTargetAsync(connection, address);
            await CleanupAccountAsync(connection, accountKey);
        }
    }

    [Fact]
    public async Task CreatesTarget_ForIpShapedAddress_ClassifiedAsIPAddress()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        // A real IP could collide with another test run's address; keep it
        // dotted-quad-shaped but derive an unlikely-to-collide last octet.
        var address = $"10.99.{Random.Shared.Next(1, 254)}.{Random.Shared.Next(1, 254)}";
        var accountKey = await InsertAccountAsync(connection, address);

        try
        {
            await connection.ExecuteAsync("EXEC usp_DeriveTargetsFromAccountAddress");

            var identifierType = await connection.QuerySingleOrDefaultAsync<string?>(
                "SELECT IdentifierType FROM web.target_identifier WHERE IdentifierValue = @Address", new { Address = address });
            Assert.Equal("IPAddress", identifierType);
        }
        finally
        {
            await CleanupTargetAsync(connection, address);
            await CleanupAccountAsync(connection, accountKey);
        }
    }

    [Fact]
    public async Task DoesNotCreateDuplicateTarget_WhenAddressAlreadyMatchesAnExistingIdentifier()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var address = $"integrationtest-existing-{Guid.NewGuid():N}";

        var existingTargetKey = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore) OUTPUT inserted.TargetKey VALUES ((SELECT TargetTypeKey FROM web.dim_target_type WHERE TypeCode = 'Server'), @TargetName, 800)",
            new { TargetName = $"IntegrationTest_{Guid.NewGuid():N}" });
        await connection.ExecuteAsync(
            "INSERT INTO web.target_identifier (TargetKey, IdentifierType, IdentifierValue) VALUES (@TargetKey, 'FQDN', @Address)",
            new { TargetKey = existingTargetKey, Address = address });

        var accountKey = await InsertAccountAsync(connection, address);

        try
        {
            await connection.ExecuteAsync("EXEC usp_DeriveTargetsFromAccountAddress");

            var matchingTargetKeys = await connection.QueryAsync<int>(
                "SELECT TargetKey FROM web.target_identifier WHERE IdentifierValue = @Address", new { Address = address });
            Assert.Equal(existingTargetKey, Assert.Single(matchingTargetKeys)); // no new row created -- the existing FQDN match already covers this Address
        }
        finally
        {
            await CleanupAccountAsync(connection, accountKey);
            await connection.ExecuteAsync("DELETE FROM web.target_identifier WHERE TargetKey = @TargetKey", new { TargetKey = existingTargetKey });
            await connection.ExecuteAsync("DELETE FROM web.dim_target WHERE TargetKey = @TargetKey", new { TargetKey = existingTargetKey });
        }
    }

    [Fact]
    public async Task Idempotent_RunningTwice_DoesNotDuplicate()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var address = $"integrationtest-idempotent-{Guid.NewGuid():N}";
        var accountKey = await InsertAccountAsync(connection, address);

        try
        {
            await connection.ExecuteAsync("EXEC usp_DeriveTargetsFromAccountAddress");
            await connection.ExecuteAsync("EXEC usp_DeriveTargetsFromAccountAddress");

            var count = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM web.target_identifier WHERE IdentifierValue = @Address", new { Address = address });
            Assert.Equal(1, count);
        }
        finally
        {
            await CleanupTargetAsync(connection, address);
            await CleanupAccountAsync(connection, accountKey);
        }
    }

    /// <summary>Confirms the deliberate scoping decision: this procedure only populates the Target inventory, it never links the account itself.</summary>
    [Fact]
    public async Task DoesNotCreateAccountTargetMapLink()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var address = $"integrationtest-nolink-{Guid.NewGuid():N}";
        var accountKey = await InsertAccountAsync(connection, address);

        try
        {
            await connection.ExecuteAsync("EXEC usp_DeriveTargetsFromAccountAddress");

            var linkCount = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM web.account_target_map WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            Assert.Equal(0, linkCount);
        }
        finally
        {
            await CleanupTargetAsync(connection, address);
            await CleanupAccountAsync(connection, accountKey);
        }
    }
}
