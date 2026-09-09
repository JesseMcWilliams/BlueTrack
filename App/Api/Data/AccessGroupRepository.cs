using Dapper;
using BlueTrack.Api.Models;
using BlueTrack.Api.RiskScoring;

namespace BlueTrack.Api.Data;

/// <summary>Backs the new Access Groups admin page (Design_Risk_Scoring.md, D-101-105, Phase A).</summary>
public sealed class AccessGroupRepository(IDbConnectionFactory connectionFactory)
{
    // D-121: sort field whitelist -- the requested field comes straight off
    // the query string, so this guards against SQL injection in the ORDER
    // BY clause (same pattern as AccountProgressRepository/RiskExceptionRepository).
    private static readonly IReadOnlyDictionary<string, string> SortableColumns = new Dictionary<string, string>
    {
        ["groupName"] = "g.GroupName",
        ["groupIdentifier"] = "g.GroupIdentifier",
        ["groupScope"] = "g.GroupScope",
        ["sorTypeName"] = "st.SorTypeName",
        ["baseRiskScore"] = "g.BaseRiskScore",
        ["computedRiskScore"] = "g.ComputedRiskScore",
        ["modifiedDate"] = "g.ModifiedDate"
    };

    private const string SelectSql = """
        SELECT g.AccessGroupKey, g.GroupName, g.GroupIdentifier, g.GroupScope, g.FoundOnTargetKey,
               t.TargetName AS FoundOnTargetName, g.DiscoverySource, g.SorTypeKey, st.SorTypeName, g.SorAddress,
               g.BaseRiskScore, g.ComputedRiskScore, g.IsRiskScoreStale, g.Description, g.ModifiedDate
        FROM web.dim_access_group g
        LEFT JOIN web.dim_target t ON t.TargetKey = g.FoundOnTargetKey
        LEFT JOIN web.dim_sor_type st ON st.SorTypeKey = g.SorTypeKey
        """;

    /// <summary>D-121: stacked filters (scope/SOR type) plus multi-column sort, same pattern as the D-42 pages.</summary>
    public async Task<IReadOnlyList<AccessGroupSummary>> GetAllAsync(
        string? groupScope = null,
        string? sorTypeName = null,
        IReadOnlyList<(string Field, bool Descending)>? sortBy = null)
    {
        using var connection = connectionFactory.Create();
        var sql = $"""
            {SelectSql}
            WHERE (@GroupScope IS NULL OR g.GroupScope = @GroupScope)
              AND (@SorTypeName IS NULL OR st.SorTypeName = @SorTypeName)
            ORDER BY {BuildOrderByClause(sortBy)}
            """;
        var rows = await connection.QueryAsync<AccessGroupSummary>(sql, new { GroupScope = groupScope, SorTypeName = sorTypeName });
        return rows.AsList();
    }

    /// <summary>D-121: the grand total row count under the same base (no filter) condition -- backs the X-Total-Count response header.</summary>
    public async Task<int> GetTotalCountAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.dim_access_group");
    }

    private static string BuildOrderByClause(IReadOnlyList<(string Field, bool Descending)>? sortBy)
    {
        if (sortBy is not { Count: > 0 })
        {
            return "g.GroupName ASC";
        }

        var clauses = sortBy
            .Where(s => SortableColumns.ContainsKey(s.Field))
            .Select(s => $"{SortableColumns[s.Field]} {(s.Descending ? "DESC" : "ASC")}")
            .ToList();

        return clauses.Count > 0 ? string.Join(", ", clauses) : "g.GroupName ASC";
    }

    /// <summary>D-121: web.dim_sor_type reference data for the SOR Type dropdown, mirroring TargetRepository.GetIdentifierTypesAsync()'s shape.</summary>
    public async Task<IReadOnlyList<SorTypeSummary>> GetSorTypesAsync()
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<SorTypeSummary>("SELECT SorTypeKey, SorTypeName FROM web.dim_sor_type ORDER BY SorTypeName");
        return rows.AsList();
    }

    public async Task<int> CreateAsync(SaveAccessGroupRequest request, int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            INSERT INTO web.dim_access_group
                (GroupName, GroupIdentifier, GroupScope, FoundOnTargetKey, DiscoverySource, SorTypeKey, SorAddress, BaseRiskScore, Description,
                 IsRiskScoreStale, CreatedBy, ModifiedBy, ModifiedDate)
            OUTPUT inserted.AccessGroupKey
            VALUES
                (@GroupName, @GroupIdentifier, @GroupScope, @FoundOnTargetKey, @DiscoverySource, @SorTypeKey, @SorAddress, @BaseRiskScore, @Description,
                 1, @ModifiedBy, @ModifiedBy, SYSUTCDATETIME())
            """;
        return await connection.QuerySingleAsync<int>(sql, new
        {
            request.GroupName,
            request.GroupIdentifier,
            request.GroupScope,
            request.FoundOnTargetKey,
            request.DiscoverySource,
            request.SorTypeKey,
            request.SorAddress,
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
                FoundOnTargetKey = @FoundOnTargetKey, DiscoverySource = @DiscoverySource, SorTypeKey = @SorTypeKey,
                SorAddress = @SorAddress, BaseRiskScore = @BaseRiskScore,
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
            request.SorTypeKey,
            request.SorAddress,
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
