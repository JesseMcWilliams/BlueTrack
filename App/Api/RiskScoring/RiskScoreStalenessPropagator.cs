using System.Data;
using Dapper;

namespace BlueTrack.Api.RiskScoring;

/// <summary>
/// Design_Risk_Scoring.md, D-119 Phase D: staleness is set explicitly
/// everywhere (this app has no triggers) whenever a Target's RiskScore or
/// an Access Group's BaseRiskScore changes. web.ufn_ReachableRiskValues
/// flattens an Account straight through to these raw values -- never
/// through an Access Group's own ComputedRiskScore -- so marking only the
/// edited Target/Access Group row stale is not enough; every Account (and,
/// for a Target edit, every Access Group) that can currently reach it must
/// be marked stale too, mirroring ufn_ReachableRiskValues' own reachability
/// paths in reverse.
/// </summary>
internal static class RiskScoreStalenessPropagator
{
    public static Task MarkEntitiesReachingTargetStaleAsync(IDbConnection connection, IDbTransaction transaction, int targetKey) =>
        MarkStaleAsync(connection, transaction, targetKey, groupsSql: """
            UPDATE ag SET IsRiskScoreStale = 1
            FROM web.dim_access_group ag
            JOIN web.access_group_target_map agtm ON agtm.AccessGroupKey = ag.AccessGroupKey
            WHERE agtm.TargetKey = @EntityKey;

            UPDATE web.dim_access_group SET IsRiskScoreStale = 1
            WHERE FoundOnTargetKey = @EntityKey AND GroupScope = 'Local';
            """, accountsSql: """
            SELECT DISTINCT atm.AccountKey FROM web.account_target_map atm WHERE atm.TargetKey = @EntityKey
            UNION
            SELECT DISTINCT aagm.AccountKey FROM web.account_access_group_map aagm JOIN web.access_group_target_map agtm ON agtm.AccessGroupKey = aagm.AccessGroupKey WHERE agtm.TargetKey = @EntityKey
            UNION
            SELECT DISTINCT aagm.AccountKey FROM web.account_access_group_map aagm JOIN web.dim_access_group ag ON ag.AccessGroupKey = aagm.AccessGroupKey WHERE ag.FoundOnTargetKey = @EntityKey AND ag.GroupScope = 'Local'
            """);

    public static Task MarkEntitiesReachingAccessGroupStaleAsync(IDbConnection connection, IDbTransaction transaction, int accessGroupKey) =>
        MarkStaleAsync(connection, transaction, accessGroupKey, groupsSql: null, accountsSql: """
            SELECT DISTINCT aagm.AccountKey FROM web.account_access_group_map aagm WHERE aagm.AccessGroupKey = @EntityKey
            """);

    private static async Task MarkStaleAsync(IDbConnection connection, IDbTransaction transaction, int entityKey, string? groupsSql, string accountsSql)
    {
        if (groupsSql is not null)
        {
            await connection.ExecuteAsync(groupsSql, new { EntityKey = entityKey }, transaction);
        }

        await connection.ExecuteAsync($"""
            INSERT INTO web.account_risk_score (AccountKey, IsRiskScoreStale)
            SELECT r.AccountKey, 1
            FROM ({accountsSql}) r
            WHERE NOT EXISTS (SELECT 1 FROM web.account_risk_score ars WHERE ars.AccountKey = r.AccountKey)
            """, new { EntityKey = entityKey }, transaction);

        await connection.ExecuteAsync($"""
            UPDATE ars SET IsRiskScoreStale = 1
            FROM web.account_risk_score ars
            JOIN ({accountsSql}) r ON r.AccountKey = ars.AccountKey
            """, new { EntityKey = entityKey }, transaction);
    }
}
