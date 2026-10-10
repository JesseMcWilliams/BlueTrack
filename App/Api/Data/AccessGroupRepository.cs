using System.Data;
using Dapper;
using BlueTrack.Api.Models;
using BlueTrack.Api.RiskScoring;

namespace BlueTrack.Api.Data;

/// <summary>Backs the new Access Groups admin page (Design_Risk-Scoring.md, D-101-105, Phase A).</summary>
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

    // D-124 Phase 3: shared between GetAllAsync and GetFilteredCountAsync so
    // the filtered-count query mirrors the list query's own WHERE clause
    // exactly -- the only real difference is paging/ORDER BY, which a COUNT
    // doesn't need.
    private const string FilterWhereSql = """
        WHERE (@GroupScope IS NULL OR g.GroupScope = @GroupScope)
          AND (@SorTypeName IS NULL OR st.SorTypeName = @SorTypeName)
        """;

    /// <summary>D-121: stacked filters (scope/SOR type) plus multi-column sort, same pattern as the D-42 pages. D-124 Phase 3: page/pageSize add SQL Server OFFSET/FETCH paging after the ORDER BY.</summary>
    public async Task<IReadOnlyList<AccessGroupSummary>> GetAllAsync(
        string? groupScope = null,
        string? sorTypeName = null,
        IReadOnlyList<(string Field, bool Descending)>? sortBy = null,
        int? page = null,
        int? pageSize = null)
    {
        using var connection = connectionFactory.Create();
        var (normalizedPage, normalizedPageSize) = PagingParams.Normalize(page, pageSize);
        var sql = $"""
            {SelectSql}
            {FilterWhereSql}
            ORDER BY {BuildOrderByClause(sortBy)}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """;
        var rows = await connection.QueryAsync<AccessGroupSummary>(sql, new
        {
            GroupScope = groupScope,
            SorTypeName = sorTypeName,
            Offset = PagingParams.Offset(normalizedPage, normalizedPageSize),
            PageSize = normalizedPageSize
        });
        return rows.AsList();
    }

    /// <summary>
    /// D-124 Phase 4: a single Access Group by key -- backs the new routed
    /// Access Group Edit page (App/Web/src/views/AccessGroupEdit.vue), which
    /// needs to load one specific row directly rather than relying on an
    /// already-loaded list page, the way GetByKeyAsync already exists on
    /// RiskExceptionRepository for the same reason. Unlike GetAllAsync, this
    /// is never paginated -- a lookup by its own primary key needs no
    /// OFFSET/FETCH at all.
    /// </summary>
    public async Task<AccessGroupSummary?> GetByKeyAsync(int accessGroupKey)
    {
        using var connection = connectionFactory.Create();
        var sql = $"""
            {SelectSql}
            WHERE g.AccessGroupKey = @AccessGroupKey
            """;
        return await connection.QuerySingleOrDefaultAsync<AccessGroupSummary>(sql, new { AccessGroupKey = accessGroupKey });
    }

    /// <summary>
    /// AD Account Discovery feature (2026-09-16): every Access Group's
    /// AccessGroupKey/GroupName/GroupIdentifier, unpaginated -- GetAllAsync
    /// pages by design (D-124 Phase 3), which would silently truncate the set
    /// AdAccountDiscoveryService needs to bind every AD group in AD, not just
    /// one page of them.
    /// </summary>
    public async Task<IReadOnlyList<(int AccessGroupKey, string GroupName, string GroupIdentifier)>> GetAllIdentifiersAsync()
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<(int, string, string)>(
            "SELECT AccessGroupKey, GroupName, GroupIdentifier FROM web.dim_access_group");
        return rows.AsList();
    }

    /// <summary>D-121: the grand total row count under the same base (no filter) condition -- backs the X-Total-Count response header.</summary>
    public async Task<int> GetTotalCountAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.dim_access_group");
    }

    /// <summary>D-124 Phase 3: how many rows match the current filter (ignoring paging) -- backs the new X-Filtered-Count header, distinct from GetTotalCountAsync's unfiltered grand total.</summary>
    public async Task<int> GetFilteredCountAsync(string? groupScope = null, string? sorTypeName = null)
    {
        using var connection = connectionFactory.Create();
        var sql = $"""
            SELECT COUNT(*)
            FROM web.dim_access_group g
            LEFT JOIN web.dim_sor_type st ON st.SorTypeKey = g.SorTypeKey
            {FilterWhereSql}
            """;
        return await connection.QuerySingleAsync<int>(sql, new { GroupScope = groupScope, SorTypeName = sorTypeName });
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
        await EnsureNoDuplicateAsync(connection, request, excludingAccessGroupKey: null);
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

        await EnsureNoDuplicateAsync(connection, request, excludingAccessGroupKey: accessGroupKey, transaction);

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

    /// <summary>D-190: "Select all matching" -- the keys of access groups matching the list's filters, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<int>> GetFilteredKeysAsync(string? groupScope, string? sorTypeName, int limit)
    {
        using var connection = connectionFactory.Create();
        var sql = $"SELECT TOP (@Limit) g.AccessGroupKey FROM web.dim_access_group g\nLEFT JOIN web.dim_sor_type st ON st.SorTypeKey = g.SorTypeKey\n{FilterWhereSql}\nORDER BY g.GroupName, g.AccessGroupKey";
        return (await connection.QueryAsync<int>(sql, new { GroupScope = groupScope, SorTypeName = sorTypeName, Limit = limit })).AsList();
    }

    /// <summary>
    /// D-122: uniqueness is (GroupName, GroupIdentifier, FoundOnTargetKey),
    /// NOT GroupIdentifier alone -- a Local group's SID (e.g. BUILTIN\
    /// Administrators, S-1-5-32-544) is identical on every server, so the
    /// same name+SID legitimately repeats once per distinct FoundOnTargetKey.
    /// FoundOnTargetKey is NULL for Domain-scope groups, and NULL <> NULL in
    /// SQL, so a naive equality check would let a Domain group duplicate
    /// silently -- the OR clause below treats two NULLs as equal for this
    /// comparison, matching the NULL-safe DB constraint
    /// (FoundOnTargetKeyForUniqueness, 50_BlueTrack_AccessGroupUniquenessFix.sql).
    /// </summary>
    private static async Task EnsureNoDuplicateAsync(IDbConnection connection, SaveAccessGroupRequest request, int? excludingAccessGroupKey, IDbTransaction? transaction = null)
    {
        const string sql = """
            SELECT 1 FROM web.dim_access_group
            WHERE (@ExcludingAccessGroupKey IS NULL OR AccessGroupKey <> @ExcludingAccessGroupKey)
              AND GroupName = @GroupName
              AND GroupIdentifier = @GroupIdentifier
              AND (FoundOnTargetKey = @FoundOnTargetKey OR (FoundOnTargetKey IS NULL AND @FoundOnTargetKey IS NULL))
            """;
        var duplicate = await connection.QuerySingleOrDefaultAsync<int?>(sql, new
        {
            request.GroupName,
            request.GroupIdentifier,
            request.FoundOnTargetKey,
            ExcludingAccessGroupKey = excludingAccessGroupKey
        }, transaction);

        if (duplicate is not null)
        {
            var foundOnDescription = request.FoundOnTargetKey is null ? "no Target (Domain scope)" : $"Target #{request.FoundOnTargetKey}";
            throw new DuplicateAccessGroupException(
                $"An Access Group named '{request.GroupName}' with identifier '{request.GroupIdentifier}' already exists for {foundOnDescription}.");
        }
    }
}
