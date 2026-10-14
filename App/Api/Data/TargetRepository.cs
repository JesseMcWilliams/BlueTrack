using Dapper;
using BlueTrack.Api.Models;
using BlueTrack.Api.RiskScoring;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the new Targets admin page (Design_Risk-Scoring.md, D-101-105,
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
               a.ApplicationName, t.RiskScore, t.Description, t.DiscoverySource, t.ModifiedDate,
               lt.PrimaryTargetKey AS LinkedToTargetKey, pt.TargetName AS LinkedToTargetName
        FROM web.dim_target t
        JOIN web.dim_target_type dtt ON dtt.TargetTypeKey = t.TargetTypeKey
        LEFT JOIN web.dim_application a ON a.ApplicationKey = t.ApplicationKey
        LEFT JOIN web.vw_linked_target lt ON lt.TargetKey = t.TargetKey
        LEFT JOIN web.dim_target pt ON pt.TargetKey = lt.PrimaryTargetKey
        """;

    // D-124 Phase 3: shared between GetAllAsync and GetFilteredCountAsync so
    // the filtered-count query mirrors the list query's own WHERE clause
    // exactly -- the only real difference is paging/ORDER BY, which a COUNT
    // doesn't need.
    private const string FilterWhereSql = """
        WHERE (@TargetTypeKey IS NULL OR t.TargetTypeKey = @TargetTypeKey)
          AND (@ApplicationKey IS NULL OR t.ApplicationKey = @ApplicationKey)
          -- D-229: a target linked to another is hidden (its primary shows) unless asked for.
          AND (@LinkedFilter = 'Show' OR NOT EXISTS (SELECT 1 FROM web.vw_linked_target hide WHERE hide.TargetKey = t.TargetKey))
        """;

    /// <summary>D-229: linked targets are hidden ("Hide", the default) or shown too ("Show"), as D-212's linked feed accounts.</summary>
    public static string NormalizeLinked(string? linked) =>
        string.Equals(linked, "show", StringComparison.OrdinalIgnoreCase) ? "Show" : "Hide";

    /// <summary>D-121: stacked filters (type/application) plus multi-column sort, same pattern as the D-42 pages. D-124 Phase 2: the type filter is now the FK key, not the old raw TargetType string. D-124 Phase 3: page/pageSize add SQL Server OFFSET/FETCH paging after the ORDER BY -- applied to the parent Target rows only, before the identifier enrichment query below, so a page is always exactly pageSize Targets (with however many identifiers each has), not pageSize Target/identifier rows.</summary>
    public async Task<IReadOnlyList<TargetSummary>> GetAllAsync(
        int? targetTypeKey = null,
        int? applicationKey = null,
        IReadOnlyList<(string Field, bool Descending)>? sortBy = null,
        int? page = null,
        int? pageSize = null,
        string? linked = null)
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
            LinkedFilter = NormalizeLinked(linked),
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
            Identifiers = identifiersByTarget.GetValueOrDefault(t.TargetKey, []),
            LinkedToTargetKey = t.LinkedToTargetKey,
            LinkedToTargetName = t.LinkedToTargetName
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
        var linkedTargets = await connection.QueryAsync<LinkedTargetSummary>("""
            SELECT t.TargetKey, t.TargetName, dtt.DisplayName AS TargetTypeDisplayName, t.DiscoverySource, lt.LinkedDate
            FROM web.vw_linked_target lt
            JOIN web.dim_target t ON t.TargetKey = lt.TargetKey
            JOIN web.dim_target_type dtt ON dtt.TargetTypeKey = t.TargetTypeKey
            WHERE lt.PrimaryTargetKey = @TargetKey
            ORDER BY t.TargetName, t.TargetKey
            """, new { TargetKey = targetKey });

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
            Identifiers = identifierRows.AsList(),
            LinkedToTargetKey = target.LinkedToTargetKey,
            LinkedToTargetName = target.LinkedToTargetName,
            LinkedTargets = linkedTargets.AsList()
        };
    }

    /// <summary>D-225: the most results a typeahead search returns.</summary>
    public const int SearchResultLimit = 20;

    /// <summary>
    /// D-225: typeahead search for Target Match Review's link target (D-229) --
    /// Targets whose name or any identifier value contains <paramref name="text"/>
    /// (a parameterised LIKE, wildcards escaped by SqlLike), names that start
    /// with it first, at most <see cref="SearchResultLimit"/>. Each result
    /// carries up to three identifiers, the matching ones first.
    /// </summary>
    public async Task<IReadOnlyList<TargetSearchResult>> SearchAsync(string text)
    {
        using var connection = connectionFactory.Create();
        var contains = SqlLike.Contains(text);
        var targets = (await connection.QueryAsync<(int TargetKey, string TargetName, string TargetTypeDisplayName)>("""
            SELECT TOP (@Limit) t.TargetKey, t.TargetName, dtt.DisplayName AS TargetTypeDisplayName
            FROM web.dim_target t
            JOIN web.dim_target_type dtt ON dtt.TargetTypeKey = t.TargetTypeKey
            WHERE (t.TargetName LIKE @Contains
                   OR EXISTS (SELECT 1 FROM web.target_identifier ti WHERE ti.TargetKey = t.TargetKey AND ti.IdentifierValue LIKE @Contains))
              -- D-229: a linked target is never offered; its primary is.
              AND NOT EXISTS (SELECT 1 FROM web.vw_linked_target lt WHERE lt.TargetKey = t.TargetKey)
            ORDER BY CASE WHEN t.TargetName LIKE @StartsWith THEN 0 ELSE 1 END, t.TargetName, t.TargetKey
            """, new { Limit = SearchResultLimit, Contains = contains, StartsWith = SqlLike.StartsWith(text) })).ToList();
        if (targets.Count == 0)
        {
            return [];
        }

        var identifierRows = await connection.QueryAsync<(int TargetKey, string IdentifierType, string IdentifierValue)>(
            "SELECT TargetKey, IdentifierType, IdentifierValue FROM web.target_identifier WHERE TargetKey IN @Keys ORDER BY IdentifierType",
            new { Keys = targets.Select(t => t.TargetKey).ToList() });
        var identifiersByTarget = identifierRows
            .GroupBy(r => r.TargetKey)
            .ToDictionary(g => g.Key, g => g
                .OrderBy(r => r.IdentifierValue.Contains(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .Take(3)
                .Select(r => new TargetIdentifier { IdentifierType = r.IdentifierType, IdentifierValue = r.IdentifierValue })
                .ToList());

        return targets.Select(t => new TargetSearchResult
        {
            TargetKey = t.TargetKey,
            TargetName = t.TargetName,
            TargetTypeDisplayName = t.TargetTypeDisplayName,
            Identifiers = identifiersByTarget.GetValueOrDefault(t.TargetKey, [])
        }).ToList();
    }

    /// <summary>D-121: the grand total row count under the same base (no filter) condition -- backs the X-Total-Count response header. D-229: linked targets aren't counted (as linked feed accounts, D-212).</summary>
    public async Task<int> GetTotalCountAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM web.dim_target t WHERE NOT EXISTS (SELECT 1 FROM web.vw_linked_target lt WHERE lt.TargetKey = t.TargetKey)");
    }

    /// <summary>D-124 Phase 3: how many Targets match the current filter (ignoring paging) -- backs the new X-Filtered-Count header, distinct from GetTotalCountAsync's unfiltered grand total.</summary>
    public async Task<int> GetFilteredCountAsync(int? targetTypeKey = null, int? applicationKey = null, string? linked = null)
    {
        using var connection = connectionFactory.Create();
        var sql = $"""
            SELECT COUNT(*)
            FROM web.dim_target t
            {FilterWhereSql}
            """;
        return await connection.QuerySingleAsync<int>(sql, new { TargetTypeKey = targetTypeKey, ApplicationKey = applicationKey, LinkedFilter = NormalizeLinked(linked) });
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
        // D-229: deleting a primary unlinks its linked targets (they score at their own risk again);
        // deleting a linked target drops its link. Link history goes with them.
        var unlinkedKeys = await connection.QueryAsync<int>(
            "SELECT TargetKey FROM web.vw_linked_target WHERE PrimaryTargetKey = @TargetKey", new { TargetKey = targetKey }, transaction);
        foreach (var unlinkedKey in unlinkedKeys)
        {
            await RiskScoreStalenessPropagator.MarkEntitiesReachingTargetStaleAsync(connection, transaction, unlinkedKey);
        }
        await connection.ExecuteAsync("DELETE FROM web.target_link WHERE LinkedTargetKey = @TargetKey OR PrimaryTargetKey = @TargetKey", new { TargetKey = targetKey }, transaction);
        await connection.ExecuteAsync("DELETE FROM web.target_identifier WHERE TargetKey = @TargetKey", new { TargetKey = targetKey }, transaction);
        await connection.ExecuteAsync("DELETE FROM web.dim_target WHERE TargetKey = @TargetKey", new { TargetKey = targetKey }, transaction);
        transaction.Commit();
    }

    private sealed class UnlinkedRow
    {
        public int PrimaryTargetKey { get; init; }
        public required string PrimaryTargetName { get; init; }
    }

    /// <summary>
    /// D-229: unlinks <paramref name="targetKey"/> from its primary. The link
    /// row stays, marked unlinked, so the pair is never linked again
    /// automatically. Everything reaching the target is marked stale: it
    /// scores at its own risk again. Null if it isn't linked; 404 if it doesn't exist.
    /// </summary>
    public async Task<(int PrimaryTargetKey, string PrimaryTargetName, string TargetName)?> UnlinkAsync(int targetKey, int? unlinkedByUserKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var targetName = await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT TargetName FROM web.dim_target WHERE TargetKey = @TargetKey", new { TargetKey = targetKey }, transaction)
            ?? throw Errors.ApiProblemException.NotFound($"Target {targetKey} doesn't exist.");
        var unlinked = await connection.QuerySingleOrDefaultAsync<UnlinkedRow>("""
            DECLARE @Unlinked TABLE (PrimaryTargetKey INT);
            UPDATE web.target_link SET UnlinkedDate = SYSUTCDATETIME(), UnlinkedBy = @UserKey
            OUTPUT inserted.PrimaryTargetKey INTO @Unlinked
            WHERE LinkedTargetKey = @TargetKey AND UnlinkedDate IS NULL;
            SELECT u.PrimaryTargetKey, t.TargetName AS PrimaryTargetName
            FROM @Unlinked u JOIN web.dim_target t ON t.TargetKey = u.PrimaryTargetKey;
            """, new { TargetKey = targetKey, UserKey = unlinkedByUserKey }, transaction);
        if (unlinked is null)
        {
            return null;
        }

        await RiskScoreStalenessPropagator.MarkEntitiesReachingTargetStaleAsync(connection, transaction, targetKey);
        transaction.Commit();
        return (unlinked.PrimaryTargetKey, unlinked.PrimaryTargetName, targetName);
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

    /// <summary>D-190: "Select all matching" -- the keys of targets matching the list's filters, at most <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<int>> GetFilteredKeysAsync(int? targetTypeKey, int? applicationKey, int limit, string? linked = null)
    {
        using var connection = connectionFactory.Create();
        var sql = $"SELECT TOP (@Limit) t.TargetKey FROM web.dim_target t\n{FilterWhereSql}\nORDER BY t.TargetName, t.TargetKey";
        return (await connection.QueryAsync<int>(sql, new { TargetTypeKey = targetTypeKey, ApplicationKey = applicationKey, Limit = limit, LinkedFilter = NormalizeLinked(linked) })).AsList();
    }
}
