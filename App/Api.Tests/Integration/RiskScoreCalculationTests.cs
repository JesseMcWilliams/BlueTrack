using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// Design_Risk_Scoring.md, D-101-105, D-119 Phase D: both candidate
/// scoring algorithms, the dispatcher, deduplication (a Target reached
/// through two different Access Groups counts once), and
/// usp_RecalculateRiskScores' staleness/override handling -- all exercised
/// against real, hand-calculable values so the expected result is checked
/// by arithmetic, not just "it ran without error".
/// </summary>
public class RiskScoreCalculationTests
{
    [Fact]
    public async Task DominantPlusTailAndCombinedExposure_MatchHandCalculatedValues_ForAKnownGraph()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        // Graph: Account -> Group (Base=100) -> Targets (400, 200).
        // Reachable value set = {400, 200, 100}.
        // DominantPlusTail (decay 0.4): 400 + 200*0.4 + 100*0.16 = 400+80+16 = 496.
        // CombinedExposure: 1000*(1-(0.6*0.8*0.9)) = 1000*(1-0.432) = 568.
        // D-124 Phase 2: TargetType is now an FK -- resolved by TypeCode via a
        // subquery rather than assuming a specific IDENTITY value.
        var targetKey1 = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore) OUTPUT inserted.TargetKey VALUES ((SELECT TargetTypeKey FROM web.dim_target_type WHERE TypeCode = 'Server'), @Name, 400)",
            new { Name = $"IntegrationTest_{Guid.NewGuid():N}" });
        var targetKey2 = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore) OUTPUT inserted.TargetKey VALUES ((SELECT TargetTypeKey FROM web.dim_target_type WHERE TypeCode = 'Server'), @Name, 200)",
            new { Name = $"IntegrationTest_{Guid.NewGuid():N}" });
        var groupKey = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_access_group (GroupName, GroupIdentifier, GroupScope, BaseRiskScore) OUTPUT inserted.AccessGroupKey VALUES ('IntegrationTest Group', @Identifier, 'Domain', 100)",
            new { Identifier = $"IntegrationTest_{Guid.NewGuid():N}" });
        await connection.ExecuteAsync(
            "INSERT INTO web.access_group_target_map (AccessGroupKey, TargetKey) VALUES (@GroupKey, @TargetKey1), (@GroupKey, @TargetKey2)",
            new { GroupKey = groupKey, TargetKey1 = targetKey1, TargetKey2 = targetKey2 });
        var accountKey = await connection.QuerySingleAsync<long>(
            "INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted) OUTPUT inserted.AccountKey VALUES (1, @SourceAccountId, 'IntegrationTest RiskScore Account', 0)",
            new { SourceAccountId = $"IntegrationTest_{Guid.NewGuid():N}" });
        await connection.ExecuteAsync(
            "INSERT INTO web.account_access_group_map (AccountKey, AccessGroupKey) VALUES (@AccountKey, @GroupKey)",
            new { AccountKey = accountKey, GroupKey = groupKey });

        try
        {
            var values = (await connection.QueryAsync<int>(
                "SELECT RiskValue FROM web.ufn_ReachableRiskValues('Account', @AccountKey) ORDER BY RiskValue DESC",
                new { AccountKey = accountKey })).ToList();
            Assert.Equal([400, 200, 100], values);

            var dominantScore = await CallScoreProcAsync(connection, "usp_CalculateRiskScore_DominantPlusTail", accountKey);
            Assert.Equal(496, dominantScore);

            var combinedScore = await CallScoreProcAsync(connection, "usp_CalculateRiskScore_CombinedExposure", accountKey);
            Assert.Equal(568, combinedScore);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.account_access_group_map WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM web.access_group_target_map WHERE AccessGroupKey = @GroupKey", new { GroupKey = groupKey });
            await connection.ExecuteAsync("DELETE FROM web.dim_access_group WHERE AccessGroupKey = @GroupKey", new { GroupKey = groupKey });
            await connection.ExecuteAsync("DELETE FROM web.dim_target WHERE TargetKey IN (@T1, @T2)", new { T1 = targetKey1, T2 = targetKey2 });
        }
    }

    [Fact]
    public async Task ReachableRiskValues_DedupesATargetReachedThroughTwoDifferentGroups()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        var sharedTargetKey = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore) OUTPUT inserted.TargetKey VALUES ((SELECT TargetTypeKey FROM web.dim_target_type WHERE TypeCode = 'Server'), @Name, 900)",
            new { Name = $"IntegrationTest_{Guid.NewGuid():N}" });
        var groupKey1 = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_access_group (GroupName, GroupIdentifier, GroupScope, BaseRiskScore) OUTPUT inserted.AccessGroupKey VALUES ('IntegrationTest Group1', @Identifier, 'Domain', 10)",
            new { Identifier = $"IntegrationTest_{Guid.NewGuid():N}" });
        var groupKey2 = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_access_group (GroupName, GroupIdentifier, GroupScope, BaseRiskScore) OUTPUT inserted.AccessGroupKey VALUES ('IntegrationTest Group2', @Identifier, 'Domain', 20)",
            new { Identifier = $"IntegrationTest_{Guid.NewGuid():N}" });
        await connection.ExecuteAsync(
            "INSERT INTO web.access_group_target_map (AccessGroupKey, TargetKey) VALUES (@G1, @T), (@G2, @T)",
            new { G1 = groupKey1, G2 = groupKey2, T = sharedTargetKey });
        var accountKey = await connection.QuerySingleAsync<long>(
            "INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted) OUTPUT inserted.AccountKey VALUES (1, @SourceAccountId, 'IntegrationTest Dedup Account', 0)",
            new { SourceAccountId = $"IntegrationTest_{Guid.NewGuid():N}" });
        await connection.ExecuteAsync(
            "INSERT INTO web.account_access_group_map (AccountKey, AccessGroupKey) VALUES (@AccountKey, @G1), (@AccountKey, @G2)",
            new { AccountKey = accountKey, G1 = groupKey1, G2 = groupKey2 });

        try
        {
            var values = (await connection.QueryAsync<int>(
                "SELECT RiskValue FROM web.ufn_ReachableRiskValues('Account', @AccountKey) ORDER BY RiskValue DESC",
                new { AccountKey = accountKey })).ToList();

            // The shared Target's 900 appears exactly once, despite being reachable via both groups; each group's own BaseRiskScore (10, 20) still appears separately.
            Assert.Equal([900, 20, 10], values);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.account_access_group_map WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM web.access_group_target_map WHERE AccessGroupKey IN (@G1, @G2)", new { G1 = groupKey1, G2 = groupKey2 });
            await connection.ExecuteAsync("DELETE FROM web.dim_access_group WHERE AccessGroupKey IN (@G1, @G2)", new { G1 = groupKey1, G2 = groupKey2 });
            await connection.ExecuteAsync("DELETE FROM web.dim_target WHERE TargetKey = @T", new { T = sharedTargetKey });
        }
    }

    [Fact]
    public async Task RecalculateRiskScores_ClearsStaleness_AndNeverOverwritesAnExistingOverride()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        var targetKey = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore) OUTPUT inserted.TargetKey VALUES ((SELECT TargetTypeKey FROM web.dim_target_type WHERE TypeCode = 'Server'), @Name, 500)",
            new { Name = $"IntegrationTest_{Guid.NewGuid():N}" });
        var accountKey = await connection.QuerySingleAsync<long>(
            "INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted) OUTPUT inserted.AccountKey VALUES (1, @SourceAccountId, 'IntegrationTest Recalc Account', 0)",
            new { SourceAccountId = $"IntegrationTest_{Guid.NewGuid():N}" });
        await connection.ExecuteAsync(
            "INSERT INTO web.account_target_map (AccountKey, TargetKey, SourceMethod) VALUES (@AccountKey, @TargetKey, 'ManualImport')",
            new { AccountKey = accountKey, TargetKey = targetKey });
        await connection.ExecuteAsync(
            "INSERT INTO web.account_risk_score (AccountKey, OverrideRiskScore, OverrideReason, IsRiskScoreStale) VALUES (@AccountKey, 777, 'Integration test override', 1)",
            new { AccountKey = accountKey });

        try
        {
            await connection.ExecuteAsync("EXEC usp_RecalculateRiskScores");

            var row = await connection.QuerySingleAsync<(int? ComputedRiskScore, int? OverrideRiskScore, int? EffectiveRiskScore, bool IsRiskScoreStale)>(
                "SELECT ComputedRiskScore, OverrideRiskScore, EffectiveRiskScore, IsRiskScoreStale FROM web.account_risk_score WHERE AccountKey = @AccountKey",
                new { AccountKey = accountKey });

            Assert.Equal(500, row.ComputedRiskScore); // recalculated from the single reachable Target
            Assert.Equal(777, row.OverrideRiskScore);  // untouched by recalculation
            Assert.Equal(777, row.EffectiveRiskScore); // override still takes precedence
            Assert.False(row.IsRiskScoreStale);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.account_target_map WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM web.account_risk_score WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM web.dim_target WHERE TargetKey = @TargetKey", new { TargetKey = targetKey });
        }
    }

    private static async Task<int> CallScoreProcAsync(SqlConnection connection, string procedureName, long accountKey)
    {
        var parameters = new DynamicParameters();
        parameters.Add("TargetSetKey", "Account");
        parameters.Add("EntityKey", accountKey);
        parameters.Add("Score", dbType: System.Data.DbType.Int32, direction: System.Data.ParameterDirection.Output);
        await connection.ExecuteAsync(procedureName, parameters, commandType: System.Data.CommandType.StoredProcedure);
        return parameters.Get<int>("Score");
    }
}
