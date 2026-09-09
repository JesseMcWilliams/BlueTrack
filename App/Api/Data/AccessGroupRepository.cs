using Dapper;
using BlueTrack.Api.Models;
using BlueTrack.Api.RiskScoring;

namespace BlueTrack.Api.Data;

/// <summary>Backs the new Access Groups admin page (Design_Risk_Scoring.md, D-101-105, Phase A).</summary>
public sealed class AccessGroupRepository(IDbConnectionFactory connectionFactory)
{
    private const string SelectSql = """
        SELECT g.AccessGroupKey, g.GroupName, g.GroupIdentifier, g.GroupScope, g.FoundOnTargetKey,
               t.TargetName AS FoundOnTargetName, g.DiscoverySource, g.BaseRiskScore, g.ComputedRiskScore,
               g.IsRiskScoreStale, g.Description, g.ModifiedDate
        FROM web.dim_access_group g
        LEFT JOIN web.dim_target t ON t.TargetKey = g.FoundOnTargetKey
        """;

    public async Task<IReadOnlyList<AccessGroupSummary>> GetAllAsync()
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<AccessGroupSummary>($"{SelectSql} ORDER BY g.GroupName");
        return rows.AsList();
    }

    public async Task<int> CreateAsync(SaveAccessGroupRequest request, int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            INSERT INTO web.dim_access_group
                (GroupName, GroupIdentifier, GroupScope, FoundOnTargetKey, DiscoverySource, BaseRiskScore, Description,
                 IsRiskScoreStale, CreatedBy, ModifiedBy, ModifiedDate)
            OUTPUT inserted.AccessGroupKey
            VALUES
                (@GroupName, @GroupIdentifier, @GroupScope, @FoundOnTargetKey, @DiscoverySource, @BaseRiskScore, @Description,
                 1, @ModifiedBy, @ModifiedBy, SYSUTCDATETIME())
            """;
        return await connection.QuerySingleAsync<int>(sql, new
        {
            request.GroupName,
            request.GroupIdentifier,
            request.GroupScope,
            request.FoundOnTargetKey,
            request.DiscoverySource,
            request.BaseRiskScore,
            request.Description,
            ModifiedBy = modifiedByUserKey
        });
    }

    public async Task UpdateAsync(int accessGroupKey, SaveAccessGroupRequest request, int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var previous = await connection.QuerySingleAsync<(int BaseRiskScore, string GroupScope, int? FoundOnTargetKey)>(
            "SELECT BaseRiskScore, GroupScope, FoundOnTargetKey FROM web.dim_access_group WHERE AccessGroupKey = @AccessGroupKey",
            new { AccessGroupKey = accessGroupKey }, transaction);

        // BaseRiskScore feeds directly into ComputedRiskScore -- any edit here
        // marks the score stale, same explicit-staleness convention Phase D's
        // load procedures will also follow for ETL-driven changes.
        const string sql = """
            UPDATE web.dim_access_group
            SET GroupName = @GroupName, GroupIdentifier = @GroupIdentifier, GroupScope = @GroupScope,
                FoundOnTargetKey = @FoundOnTargetKey, DiscoverySource = @DiscoverySource, BaseRiskScore = @BaseRiskScore,
                Description = @Description, IsRiskScoreStale = 1, ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
            WHERE AccessGroupKey = @AccessGroupKey
            """;
        await connection.ExecuteAsync(sql, new
        {
            AccessGroupKey = accessGroupKey,
            request.GroupName,
            request.GroupIdentifier,
            request.GroupScope,
            request.FoundOnTargetKey,
            request.DiscoverySource,
            request.BaseRiskScore,
            request.Description,
            ModifiedBy = modifiedByUserKey
        }, transaction);

        // BaseRiskScore, GroupScope and FoundOnTargetKey all feed into what a
        // member Account reaches per ufn_ReachableRiskValues -- any of the
        // three changing means every current member Account needs restaling,
        // not just this Access Group's own row (already marked above).
        if (request.BaseRiskScore != previous.BaseRiskScore || request.GroupScope != previous.GroupScope || request.FoundOnTargetKey != previous.FoundOnTargetKey)
        {
            await RiskScoreStalenessPropagator.MarkEntitiesReachingAccessGroupStaleAsync(connection, transaction, accessGroupKey);
        }

        transaction.Commit();
    }

    public async Task DeleteAsync(int accessGroupKey)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("DELETE FROM web.dim_access_group WHERE AccessGroupKey = @AccessGroupKey", new { AccessGroupKey = accessGroupKey });
    }
}
