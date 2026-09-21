using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// AD Account Discovery feature (2026-09-16), Database/32: the
/// AccessGroupSet variant of Phase D's scoring engine, for a candidate AD
/// account that isn't in dbo.fact_account yet (so it can't go through
/// web.ufn_ReachableRiskValues('Account', ...), which requires a real
/// AccountKey FK-valid row in web.account_access_group_map). Same
/// hand-calculable graph as RiskScoreCalculationTests' own
/// DominantPlusTailAndCombinedExposure test, confirming the AccessGroupSet
/// path produces identical numbers given the identical reachable-value set
/// -- deliberately no fact_account/account_access_group_map row created
/// anywhere in this file, proving the whole point of this addition.
/// </summary>
public class RiskScoreCalculationForAccessGroupSetTests
{
    [Fact]
    public async Task DominantPlusTailAndCombinedExposure_MatchHandCalculatedValues_WithNoAccountInvolvedAtAll()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        // Graph: Group (Base=100) -> Targets (400, 200). Reachable value set
        // = {400, 200, 100} -- identical to RiskScoreCalculationTests' own
        // Account-based test, but reached via a directly-supplied
        // AccessGroupKey set instead of account_access_group_map.
        var targetKey1 = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore) OUTPUT inserted.TargetKey VALUES ((SELECT TargetTypeKey FROM web.dim_target_type WHERE TypeCode = 'Server'), @Name, 400)",
            new { Name = $"IntegrationTest_{Guid.NewGuid():N}" });
        var targetKey2 = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore) OUTPUT inserted.TargetKey VALUES ((SELECT TargetTypeKey FROM web.dim_target_type WHERE TypeCode = 'Server'), @Name, 200)",
            new { Name = $"IntegrationTest_{Guid.NewGuid():N}" });
        var groupKey = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_access_group (GroupName, GroupIdentifier, GroupScope, BaseRiskScore) OUTPUT inserted.AccessGroupKey VALUES ('IntegrationTest AccessGroupSet Group', @Identifier, 'Domain', 100)",
            new { Identifier = $"IntegrationTest_{Guid.NewGuid():N}" });
        await connection.ExecuteAsync(
            "INSERT INTO web.access_group_target_map (AccessGroupKey, TargetKey) VALUES (@GroupKey, @TargetKey1), (@GroupKey, @TargetKey2)",
            new { GroupKey = groupKey, TargetKey1 = targetKey1, TargetKey2 = targetKey2 });

        try
        {
            var values = (await connection.QueryAsync<int>(
                "SELECT RiskValue FROM web.ufn_ReachableRiskValues_ForAccessGroupSet(@Keys) ORDER BY RiskValue DESC",
                new { Keys = BuildAccessGroupKeyList(groupKey) })).ToList();
            Assert.Equal([400, 200, 100], values);

            var dominantScore = await CallScoreProcAsync(connection, "usp_CalculateRiskScore_DominantPlusTail_ForAccessGroupSet", groupKey);
            Assert.Equal(496, dominantScore);

            var combinedScore = await CallScoreProcAsync(connection, "usp_CalculateRiskScore_CombinedExposure_ForAccessGroupSet", groupKey);
            Assert.Equal(568, combinedScore);

            var dispatchedScore = await CallScoreProcAsync(connection, "usp_CalculateRiskScoreForAccessGroupSet", groupKey);
            Assert.True(dispatchedScore is 496 or 568, $"Dispatcher returned {dispatchedScore}, expected whichever algorithm is currently active.");
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.access_group_target_map WHERE AccessGroupKey = @GroupKey", new { GroupKey = groupKey });
            await connection.ExecuteAsync("DELETE FROM web.dim_access_group WHERE AccessGroupKey = @GroupKey", new { GroupKey = groupKey });
            await connection.ExecuteAsync("DELETE FROM web.dim_target WHERE TargetKey IN (@T1, @T2)", new { T1 = targetKey1, T2 = targetKey2 });
        }
    }

    [Fact]
    public async Task ReachableRiskValues_ForAccessGroupSet_DedupesATargetReachableThroughTwoGroupsInTheSet()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        var sharedTargetKey = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore) OUTPUT inserted.TargetKey VALUES ((SELECT TargetTypeKey FROM web.dim_target_type WHERE TypeCode = 'Server'), @Name, 900)",
            new { Name = $"IntegrationTest_{Guid.NewGuid():N}" });
        var groupKey1 = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_access_group (GroupName, GroupIdentifier, GroupScope, BaseRiskScore) OUTPUT inserted.AccessGroupKey VALUES ('IntegrationTest AccessGroupSet Group1', @Identifier, 'Domain', 10)",
            new { Identifier = $"IntegrationTest_{Guid.NewGuid():N}" });
        var groupKey2 = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_access_group (GroupName, GroupIdentifier, GroupScope, BaseRiskScore) OUTPUT inserted.AccessGroupKey VALUES ('IntegrationTest AccessGroupSet Group2', @Identifier, 'Domain', 20)",
            new { Identifier = $"IntegrationTest_{Guid.NewGuid():N}" });
        await connection.ExecuteAsync(
            "INSERT INTO web.access_group_target_map (AccessGroupKey, TargetKey) VALUES (@G1, @T), (@G2, @T)",
            new { G1 = groupKey1, G2 = groupKey2, T = sharedTargetKey });

        try
        {
            var values = (await connection.QueryAsync<int>(
                "SELECT RiskValue FROM web.ufn_ReachableRiskValues_ForAccessGroupSet(@Keys) ORDER BY RiskValue DESC",
                new { Keys = BuildAccessGroupKeyList(groupKey1, groupKey2) })).ToList();

            // The shared Target's 900 appears exactly once despite being
            // reachable via both groups in the set; each group's own
            // BaseRiskScore (10, 20) still appears separately.
            Assert.Equal([900, 20, 10], values);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.access_group_target_map WHERE AccessGroupKey IN (@G1, @G2)", new { G1 = groupKey1, G2 = groupKey2 });
            await connection.ExecuteAsync("DELETE FROM web.dim_access_group WHERE AccessGroupKey IN (@G1, @G2)", new { G1 = groupKey1, G2 = groupKey2 });
            await connection.ExecuteAsync("DELETE FROM web.dim_target WHERE TargetKey = @T", new { T = sharedTargetKey });
        }
    }

    private static SqlMapper.ICustomQueryParameter BuildAccessGroupKeyList(params int[] accessGroupKeys)
    {
        var table = new DataTable();
        table.Columns.Add("AccessGroupKey", typeof(int));
        foreach (var key in accessGroupKeys)
        {
            table.Rows.Add(key);
        }
        return table.AsTableValuedParameter("web.AccessGroupKeyList");
    }

    private static async Task<int> CallScoreProcAsync(SqlConnection connection, string procedureName, params int[] accessGroupKeys)
    {
        var parameters = new DynamicParameters();
        parameters.Add("AccessGroupKeys", BuildAccessGroupKeyList(accessGroupKeys));
        parameters.Add("Score", dbType: DbType.Int32, direction: ParameterDirection.Output);
        await connection.ExecuteAsync(procedureName, parameters, commandType: CommandType.StoredProcedure);
        return parameters.Get<int>("Score");
    }
}
