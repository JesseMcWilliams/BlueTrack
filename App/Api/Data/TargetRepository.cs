using Dapper;
using BlueTrack.Api.Models;
using BlueTrack.Api.RiskScoring;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the new Targets admin page (Design_Risk_Scoring.md, D-101-105,
/// Phase A). A Target's identifiers (web.target_identifier) are managed as
/// a nested child collection -- replace-all-on-save (delete then re-insert
/// every identifier row for a Target on each update), the simplest correct
/// approach for a small per-Target set, matching how CredentialRepository
/// treats a credential's vault fields as one atomic unit rather than
/// diffing individual field changes.
/// </summary>
public sealed class TargetRepository(IDbConnectionFactory connectionFactory)
{
    // D-121: sort field whitelist -- the requested field comes straight off
    // the query string, so this guards against SQL injection in the ORDER
    // BY clause (same pattern as AccountProgressRepository/RiskExceptionRepository).
    private static readonly IReadOnlyDictionary<string, string> SortableColumns = new Dictionary<string, string>
    {
        ["targetName"] = "t.TargetName",
        ["targetType"] = "dtt.DisplayName",
        ["applicationName"] = "a.ApplicationName",
        ["riskScore"] = "t.RiskScore",
        ["modifiedDate"] = "t.ModifiedDate"
    };

    private const string SelectSql = """
        SELECT t.TargetKey, t.TargetTypeKey, dtt.TypeCode AS TargetTypeCode, dtt.DisplayName AS TargetTypeDisplayName,
               t.TargetName, t.InternalGuid, t.ApplicationKey,
               a.ApplicationName, t.RiskScore, t.Description, t.DiscoverySource, t.ModifiedDate
        FROM web.dim_target t
        JOIN web.dim_target_type dtt ON dtt.TargetTypeKey = t.TargetTypeKey
        LEFT JOIN web.dim_application a ON a.ApplicationKey = t.ApplicationKey
        """;

    // D-124 Phase 3: shared between GetAllAsync and GetFilteredCountAsync so
    // the filtered-count query mirrors the list query's own WHERE clause
    // exactly -- the only real difference is paging/ORDER BY, which a COUNT
    // doesn't need.
    private const string FilterWhereSql = """
        WHERE (@TargetTypeKey IS NULL OR t.TargetTypeKey = @TargetTypeKey)
          AND (@ApplicationKey IS NULL OR t.ApplicationKey = @ApplicationKey)
        """;

    /// <summary>D-121: stacked filters (type/application) plus multi-column sort, same pattern as the D-42 pages. D-124 Phase 2: the type filter is now the FK key, not the old raw TargetType string. D-124 Phase 3: page/pageSize add SQL Server OFFSET/FETCH paging after the ORDER BY -- applied to the parent Target rows only, before the identifier enrichment query below, so a page is always exactly pageSize Targets (with however many identifiers each has), not pageSize Target/identifier rows.</summary>
    public async Task<IReadOnlyList<TargetSummary>> GetAllAsync(
        int? targetTypeKey = null,
        int? applicationKey = null,
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
        var targets = (await connection.QueryAsync<TargetSummary>(sql, new
        {
            TargetTypeKey = targetTypeKey,
            ApplicationKey = applicationKey,
            Offset = PagingParams.Offset(normalizedPage, normalizedPageSize),
            PageSize = normalizedPageSize
        })).ToList();
        if (targets.Count == 0)
        {
            return targets;
        }

        var identifierRows = await connection.QueryAsync<(int TargetKey, string IdentifierType, string IdentifierValue)>(
            "SELECT TargetKey, IdentifierType, IdentifierValue FROM web.target_identifier ORDER BY IdentifierType");
        var identifiersByTarget = identifierRows
            .GroupBy(r => r.TargetKey)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TargetIdentifier>)g.Select(r => new TargetIdentifier { IdentifierType = r.IdentifierType, IdentifierValue = r.IdentifierValue }).ToList());

        return targets.Select(t => new TargetSummary
        {
            TargetKey = t.TargetKey,
            TargetTypeKey = t.TargetTypeKey,
            TargetTypeCode = t.TargetTypeCode,
            TargetTypeDisplayName = t.TargetTypeDisplayName,
            TargetName = t.TargetName,
            InternalGuid = t.InternalGuid,
            ApplicationKey = t.ApplicationKey,
            ApplicationName = t.ApplicationName,
            RiskScore = t.RiskScore,
            Description = t.Description,
            DiscoverySource = t.DiscoverySource,
            ModifiedDate = t.ModifiedDate,
            Identifiers = identifiersByTarget.GetValueOrDefault(t.TargetKey, [])
        }).ToList();
    }

    /// <summary>
    /// D-124 Phase 4: a single Target by key, identifiers included -- backs
    /// the new routed Target Edit page (App/Web/src/views/TargetEdit.vue),
    /// which needs to load one specific row directly (e.g. after a page
    /// refresh) rather than relying on an already-loaded list page, the way
    /// GetByKeyAsync already exists on RiskExceptionRepository for the same
    /// reason. Unlike GetAllAsync, this is never paginated -- a lookup by
    /// its own primary key needs no OFFSET/FETCH at all.
    /// </summary>
    public async Task<TargetSummary?> GetByKeyAsync(int targetKey)
    {
        using var connection = connectionFactory.Create();
        var sql = $"""
            {SelectSql}
            WHERE t.TargetKey = @TargetKey
            """;
        var target = await connection.QuerySingleOrDefaultAsync<TargetSummary>(sql, new { TargetKey = targetKey });
        if (target is null)
        {
            return null;
        }

        var identifierRows = await connection.QueryAsync<TargetIdentifier>(
            "SELECT IdentifierType, IdentifierValue FROM web.target_identifier WHERE TargetKey = @TargetKey ORDER BY IdentifierType",
            new { TargetKey = targetKey });

        return new TargetSummary
        {
            TargetKey = target.TargetKey,
            TargetTypeKey = target.TargetTypeKey,
            TargetTypeCode = target.TargetTypeCode,
            TargetTypeDisplayName = target.TargetTypeDisplayName,
            TargetName = target.TargetName,
            InternalGuid = target.InternalGuid,
            ApplicationKey = target.ApplicationKey,
            ApplicationName = target.ApplicationName,
            RiskScore = target.RiskScore,
            Description = target.Description,
            DiscoverySource = target.DiscoverySource,
            ModifiedDate = target.ModifiedDate,
            Identifiers = identifierRows.AsList()
        };
    }

    /// <summary>D-121: the grand total row count under the same base (no filter) condition -- backs the X-Total-Count response header.</summary>
    public async Task<int> GetTotalCountAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.dim_target");
    }

    /// <summary>D-124 Phase 3: how many Targets match the current filter (ignoring paging) -- backs the new X-Filtered-Count header, distinct from GetTotalCountAsync's unfiltered grand total.</summary>
    public async Task<int> GetFilteredCountAsync(int? targetTypeKey = null, int? applicationKey = null)
    {
        using var connection = connectionFactory.Create();
        var sql = $"""
            SELECT COUNT(*)
            FROM web.dim_target t
            {FilterWhereSql}
            """;
        return await connection.QuerySingleAsync<int>(sql, new { TargetTypeKey = targetTypeKey, ApplicationKey = applicationKey });
    }

    private static string BuildOrderByClause(IReadOnlyList<(string Field, bool Descending)>? sortBy)
    {
        if (sortBy is not { Count: > 0 })
        {
            return "t.TargetName ASC";
        }

        var clauses = sortBy
            .Where(s => SortableColumns.ContainsKey(s.Field))
            .Select(s => $"{SortableColumns[s.Field]} {(s.Descending ? "DESC" : "ASC")}")
            .ToList();

        return clauses.Count > 0 ? string.Join(", ", clauses) : "t.TargetName ASC";
    }

    public async Task<IReadOnlyList<TargetIdentifierTypeSummary>> GetIdentifierTypesAsync()
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<TargetIdentifierTypeSummary>(
            "SELECT IdentifierType, MatchPriority, RequiresReview FROM web.dim_target_identifier_type ORDER BY MatchPriority");
        return rows.AsList();
    }

    /// <summary>D-124 Phase 2: web.dim_target_type reference data for the Type dropdown, mirroring GetIdentifierTypesAsync()'s/AccessGroupRepository.GetSorTypesAsync()'s shape.</summary>
    public async Task<IReadOnlyList<TargetTypeSummary>> GetTargetTypesAsync()
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<TargetTypeSummary>(
            "SELECT TargetTypeKey, TypeCode, DisplayName FROM web.dim_target_type ORDER BY DisplayName");
        return rows.AsList();
    }

    public async Task<int> CreateAsync(SaveTargetRequest request, int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var targetKey = await connection.QuerySingleAsync<int>("""
            INSERT INTO web.dim_target (TargetTypeKey, TargetName, ApplicationKey, RiskScore, Description, DiscoverySource, CreatedBy, ModifiedBy, ModifiedDate)
            OUTPUT inserted.TargetKey
            VALUES (@TargetTypeKey, @TargetName, @ApplicationKey, @RiskScore, @Description, @DiscoverySource, @ModifiedBy, @ModifiedBy, SYSUTCDATETIME())
            """, new { request.TargetTypeKey, request.TargetName, request.ApplicationKey, request.RiskScore, request.Description, request.DiscoverySource, ModifiedBy = modifiedByUserKey }, transaction);

        await InsertIdentifiersAsync(connection, transaction, targetKey, request.Identifiers);

        transaction.Commit();
        return targetKey;
    }

    public async Task UpdateAsync(int targetKey, SaveTargetRequest request, int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var previousRiskScore = await connection.QuerySingleAsync<int>(
            "SELECT RiskScore FROM web.dim_target WHERE TargetKey = @TargetKey", new { TargetKey = targetKey }, transaction);

        await connection.ExecuteAsync("""
            UPDATE web.dim_target
            SET TargetTypeKey = @TargetTypeKey, TargetName = @TargetName, ApplicationKey = @ApplicationKey,
                RiskScore = @RiskScore, Description = @Description, DiscoverySource = @DiscoverySource,
                ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
            WHERE TargetKey = @TargetKey
            """, new { TargetKey = targetKey, request.TargetTypeKey, request.TargetName, request.ApplicationKey, request.RiskScore, request.Description, request.DiscoverySource, ModifiedBy = modifiedByUserKey }, transaction);

        await connection.ExecuteAsync("DELETE FROM web.target_identifier WHERE TargetKey = @TargetKey", new { TargetKey = targetKey }, transaction);
        await InsertIdentifiersAsync(connection, transaction, targetKey, request.Identifiers);

        if (request.RiskScore != previousRiskScore)
        {
            await RiskScoreStalenessPropagator.MarkEntitiesReachingTargetStaleAsync(connection, transaction, targetKey);
        }

        transaction.Commit();
    }

    public async Task DeleteAsync(int targetKey)
    {
        using var connection = connectionFactory.Create();
        // web.target_identifier has no ON DELETE CASCADE -- delete children first,
        // same explicit-cascade convention RoleRepository.DeleteRoleAsync already uses.
        connection.Open();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("DELETE FROM web.target_identifier WHERE TargetKey = @TargetKey", new { TargetKey = targetKey }, transaction);
        await connection.ExecuteAsync("DELETE FROM web.dim_target WHERE TargetKey = @TargetKey", new { TargetKey = targetKey }, transaction);
        transaction.Commit();
    }

    private static async Task InsertIdentifiersAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, int targetKey, IReadOnlyList<SaveTargetIdentifierRequest> identifiers)
    {
        if (identifiers.Count == 0)
        {
            return;
        }

        const string sql = "INSERT INTO web.target_identifier (TargetKey, IdentifierType, IdentifierValue) VALUES (@TargetKey, @IdentifierType, @IdentifierValue)";
        foreach (var identifier in identifiers)
        {
            await connection.ExecuteAsync(sql, new { TargetKey = targetKey, identifier.IdentifierType, identifier.IdentifierValue }, transaction);
        }
    }
}
