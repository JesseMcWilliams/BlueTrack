using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the Account Progress list/grid page, including D-42's "multiple
/// simultaneous layers of sort and filter" (stacked filters plus
/// multi-column sort, applied together). Sort columns are validated
/// against a fixed whitelist (SortableColumns) rather than interpolating
/// the requested field name directly -- the field names themselves come
/// from the frontend's query string, so this is the SQL-injection guard,
/// not just a defensive nicety.
/// </summary>
public sealed class AccountProgressRepository(IDbConnectionFactory connectionFactory)
{
    private static readonly IReadOnlyDictionary<string, string> SortableColumns = new Dictionary<string, string>
    {
        ["accountName"] = "fa.AccountName",
        ["userName"] = "fa.UserName",
        ["address"] = "fa.Address",
        ["stageName"] = "stg.StageOrder",
        ["statusName"] = "sts.StatusName",
        ["riskLevelName"] = "rl.RiskOrder",
        ["ownerName"] = "fap.OwnerName",
        ["targetRemediationDate"] = "fap.TargetRemediationDate",
        ["actualCompletionDate"] = "fap.ActualCompletionDate",
        ["effectiveRiskScore"] = "ars.EffectiveRiskScore",
        ["riskScoreBandName"] = "band.RiskOrder"
    };

    // D-124 Phase 3: shared between GetSummaryListAsync and
    // GetFilteredCountAsync so the filtered-count query mirrors the list
    // query's own FROM/JOIN/WHERE exactly -- the only real difference is
    // paging/ORDER BY, which a COUNT doesn't need.
    private const string FilterFromSql = """
        FROM dbo.fact_account_progress fap
        JOIN dbo.fact_account fa           ON fa.AccountKey = fap.AccountKey
        JOIN dbo.dim_blueprint_stage stg    ON stg.StageKey = fap.CurrentStageKey
        JOIN dbo.dim_progress_status sts     ON sts.StatusKey = fap.CurrentStatusKey
        LEFT JOIN dbo.dim_risk_level rl         ON rl.RiskLevelKey = fap.RiskLevelKey
        LEFT JOIN web.account_risk_score ars    ON ars.AccountKey = fa.AccountKey
        LEFT JOIN web.dim_risk_score_band band  ON ars.EffectiveRiskScore BETWEEN band.MinScore AND band.MaxScore
        LEFT JOIN web.account_deletion ad       ON ad.AccountKey = fa.AccountKey
        LEFT JOIN web.app_user adu              ON adu.UserKey = ad.DeletedBy
        JOIN dbo.dim_source_system ss           ON ss.SourceSystemKey = fa.SourceSystemKey
        OUTER APPLY (SELECT TOP (1) l.LinkedAccountKey FROM dbo.vw_linked_feed_account l WHERE l.AccountKey = fa.AccountKey) lnk
        LEFT JOIN dbo.fact_account lfa          ON lfa.AccountKey = lnk.LinkedAccountKey
        -- D-185: deleted accounts are hidden unless asked for ("Show" or "Only").
        WHERE (CASE @DeletedFilter WHEN 'Show' THEN 1 WHEN 'Only' THEN fa.IsDeleted ELSE 1 - fa.IsDeleted END) = 1
          -- D-212: a feed account linked to a CyberArk account is hidden (the CyberArk one shows) unless asked for.
          AND (@LinkedFilter = 'Show' OR lnk.LinkedAccountKey IS NULL)
          AND (@StageName IS NULL OR stg.StageName = @StageName)
          AND (@StatusName IS NULL OR sts.StatusName = @StatusName)
          AND (@RiskLevelName IS NULL OR rl.RiskLevelName = @RiskLevelName)
          AND (@OwnerContains IS NULL OR fap.OwnerName LIKE '%' + @OwnerContains + '%')
          AND (@SearchPattern IS NULL OR fa.UserName LIKE @SearchPattern OR fa.Address LIKE @SearchPattern OR fa.AccountName LIKE @SearchPattern)
          -- UI review W7-1: the KPI Summary's counts (D-143), so each count can open the accounts behind it.
          AND (@Kpi IS NULL OR @Kpi = 'total'
               OR (@Kpi = 'inScope'    AND sts.StatusName <> 'Risk Accepted / Excluded')
               OR (@Kpi = 'onboarded'  AND sts.StatusName <> 'Risk Accepted / Excluded' AND stg.StageOrder >= 3)
               OR (@Kpi = 'managed'    AND sts.StatusName <> 'Risk Accepted / Excluded' AND stg.StageOrder >= 4)
               OR (@Kpi = 'compliant'  AND stg.StageOrder = 5 AND sts.StatusName = 'Complete'))
        """;

    /// <summary>
    /// D-178: the list's search box matches Username or Address, "contains";
    /// D-191 adds AccountName, so an account with neither can still be found.
    /// Account names often contain '_' (and an address can contain '['),
    /// which LIKE would otherwise read as wildcards, so the text is matched
    /// literally: each of [ % _ is wrapped in brackets.
    /// </summary>
    private static string? ToContainsPattern(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return null;
        var escaped = search.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
        return $"%{escaped}%";
    }

    /// <summary>D-124 Phase 3: page/pageSize add SQL Server OFFSET/FETCH paging after the ORDER BY.</summary>
    public async Task<IReadOnlyList<AccountProgressSummary>> GetSummaryListAsync(
        string? stageName = null,
        string? statusName = null,
        string? riskLevelName = null,
        string? ownerContains = null,
        IReadOnlyList<(string Field, bool Descending)>? sortBy = null,
        int? page = null,
        int? pageSize = null,
        string? search = null,
        string? deleted = null,
        string? linked = null,
        string? kpi = null)
    {
        using var connection = connectionFactory.Create();
        var (normalizedPage, normalizedPageSize) = PagingParams.Normalize(page, pageSize);

        // D-186: the decommission flag (web.vw_decom_account) is looked up
        // for the page's rows only, after paging, so it never slows the
        // filter or the counts.
        var orderBy = BuildOrderByClause(sortBy);
        var sql = $"""
            SELECT page.*,
                   dca.InFlaggedSafe AS DecomInFlaggedSafe, dca.NameFlagged AS DecomNameFlagged,
                   dca.SafeName AS DecomSafeName, dca.OtherSafes AS DecomOtherSafes,
                   CAST(dbo.fn_MatchesNamePattern(isf.SafeName, iac.SafeIgnoreMode, iac.SafeIgnoreValue) AS BIT) AS IsInIgnoredSafe,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.vw_selfhosted_internal_safe iis WHERE iis.SafeKey = isf.SafeKey) THEN 1 ELSE 0 END AS BIT) AS IsInInternalSafe
            FROM (
                SELECT
                    fa.AccountKey,
                    fa.AccountName,
                    fa.UserName,
                    fa.Address,
                    stg.StageName,
                    sts.StatusName,
                    rl.RiskLevelName,
                    fap.OwnerName,
                    fap.TargetRemediationDate,
                    fap.ActualCompletionDate,
                    ars.EffectiveRiskScore,
                    band.BandName AS RiskScoreBandName,
                    fa.IsDeleted, fa.IsDeletedInSource, adu.DisplayName AS DeletedByName, ad.DeletedAt, ad.Reason AS DeletionReason,
                    ss.SourceSystemCode, ss.SourceSystemName, lnk.LinkedAccountKey AS LinkedToAccountKey, lfa.AccountName AS LinkedToAccountName,
                    ROW_NUMBER() OVER (ORDER BY {orderBy}) AS RowNo
                {FilterFromSql}
                ORDER BY {orderBy}
                OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            ) page
            OUTER APPLY (SELECT TOP 1 v.InFlaggedSafe, v.NameFlagged, v.SafeName, v.OtherSafes
                         FROM web.vw_decom_account v WHERE v.AccountKey = page.AccountKey) dca
            JOIN dbo.fact_account ifa ON ifa.AccountKey = page.AccountKey
            LEFT JOIN dbo.dim_safe isf ON isf.SafeKey = ifa.SafeKey
            CROSS JOIN web.app_config iac
            ORDER BY page.RowNo
            """;

        var rows = await connection.QueryAsync<AccountProgressSummary>(sql, new
        {
            StageName = stageName,
            StatusName = statusName,
            RiskLevelName = riskLevelName,
            OwnerContains = ownerContains,
            SearchPattern = ToContainsPattern(search),
            DeletedFilter = NormalizeDeleted(deleted),
            LinkedFilter = NormalizeLinked(linked),
            Kpi = NormalizeKpi(kpi),
            Offset = PagingParams.Offset(normalizedPage, normalizedPageSize),
            PageSize = normalizedPageSize
        });
        return rows.AsList();
    }

    /// <summary>D-121: the grand total row count under the same base "active" condition (fa.IsDeleted = 0) but ignoring the caller's filter params -- backs the X-Total-Count response header.</summary>
    public async Task<int> GetTotalCountAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.fact_account_progress fap JOIN dbo.fact_account fa ON fa.AccountKey = fap.AccountKey WHERE fa.IsDeleted = 0 AND NOT EXISTS (SELECT 1 FROM dbo.vw_linked_feed_account l WHERE l.AccountKey = fa.AccountKey)");
    }

    /// <summary>D-124 Phase 3: how many rows match the current filter (ignoring paging) -- backs the new X-Filtered-Count header, distinct from GetTotalCountAsync's unfiltered grand total.</summary>
    public async Task<int> GetFilteredCountAsync(
        string? stageName = null,
        string? statusName = null,
        string? riskLevelName = null,
        string? ownerContains = null,
        string? search = null,
        string? deleted = null,
        string? linked = null,
        string? kpi = null)
    {
        using var connection = connectionFactory.Create();
        var sql = $"SELECT COUNT(*) {FilterFromSql}";
        return await connection.QuerySingleAsync<int>(sql, new
        {
            StageName = stageName,
            StatusName = statusName,
            RiskLevelName = riskLevelName,
            OwnerContains = ownerContains,
            SearchPattern = ToContainsPattern(search),
            DeletedFilter = NormalizeDeleted(deleted),
            LinkedFilter = NormalizeLinked(linked),
            Kpi = NormalizeKpi(kpi)
        });
    }

    /// <summary>
    /// D-182: the keys of every account matching the list's filters, for the
    /// bulk edit's "Select all matching". At most <paramref name="limit"/>;
    /// the caller compares against GetFilteredCountAsync to know if there were more.
    /// </summary>
    public async Task<IReadOnlyList<long>> GetFilteredKeysAsync(
        string? stageName, string? statusName, string? riskLevelName, string? ownerContains, string? search, int limit, string? deleted = null, string? linked = null, string? kpi = null)
    {
        using var connection = connectionFactory.Create();
        var sql = $"SELECT TOP (@Limit) fa.AccountKey {FilterFromSql} ORDER BY fa.AccountName, fa.AccountKey";
        return (await connection.QueryAsync<long>(sql, new
        {
            StageName = stageName,
            StatusName = statusName,
            RiskLevelName = riskLevelName,
            OwnerContains = ownerContains,
            SearchPattern = ToContainsPattern(search),
            DeletedFilter = NormalizeDeleted(deleted),
            LinkedFilter = NormalizeLinked(linked),
            Kpi = NormalizeKpi(kpi),
            Limit = limit
        })).AsList();
    }

    /// <summary>UI review W7-1: one of the KPI Summary's counts (total, inScope, onboarded, managed, compliant), or null for no KPI filter; an unknown value is ignored.</summary>
    private static string? NormalizeKpi(string? kpi) => kpi?.ToLowerInvariant() switch
    {
        "total" => "total",
        "inscope" => "inScope",
        "onboarded" => "onboarded",
        "managed" => "managed",
        "compliant" => "compliant",
        _ => null
    };

    /// <summary>D-185: "Hide" (default), "Show" (deleted too) or "Only" (deleted only).</summary>
    private static string NormalizeDeleted(string? deleted) => deleted?.ToLowerInvariant() switch
    {
        "show" => "Show",
        "only" => "Only",
        _ => "Hide"
    };

    /// <summary>D-212: linked feed accounts are hidden ("Hide", the default) or shown too ("Show").</summary>
    private static string NormalizeLinked(string? linked) =>
        string.Equals(linked, "show", StringComparison.OrdinalIgnoreCase) ? "Show" : "Hide";

    private static string BuildOrderByClause(IReadOnlyList<(string Field, bool Descending)>? sortBy)
    {
        if (sortBy is not { Count: > 0 })
        {
            return "fa.AccountName ASC";
        }

        var clauses = sortBy
            .Where(s => SortableColumns.ContainsKey(s.Field))
            .Select(s => $"{SortableColumns[s.Field]} {(s.Descending ? "DESC" : "ASC")}")
            .ToList();

        return clauses.Count > 0 ? string.Join(", ", clauses) : "fa.AccountName ASC";
    }

    public async Task<AccountProgressDetail?> GetDetailAsync(long accountKey)
    {
        using var connection = connectionFactory.Create();
        // D-127: same web.account_risk_score/dim_risk_score_band join as
        // GetSummaryListAsync's FilterFromSql, so Calculated Risk/Risk Band/
        // Override on this page always agree with the list's own values.
        const string sql = """
            SELECT
                fap.ProgressKey, fap.AccountKey, fa.AccountName, fa.UserName, fa.Address,
                fa.CreatedTime, fa.LastCPMAction, fa.LastCPMActionResult, fa.LastCPMActionTime,
                fap.CurrentStageKey, fap.CurrentStatusKey, fap.RiskLevelKey, fap.AccountTypeKey, fap.SORKey, fap.ApplicationKey,
                fap.OwnerName, fap.BusinessUnit, fap.TargetRemediationDate, fap.ActualCompletionDate, fap.Notes,
                fap.LastUpdated, fap.ExceptionKey,
                ars.ComputedRiskScore, ars.OverrideRiskScore, ars.EffectiveRiskScore, band.BandName AS RiskScoreBandName,
                fa.IsDeleted, fa.IsDeletedInSource, adu.DisplayName AS DeletedByName, ad.DeletedAt, ad.Reason AS DeletionReason,
                dca.InFlaggedSafe AS DecomInFlaggedSafe, dca.NameFlagged AS DecomNameFlagged,
                dca.SafeName AS DecomSafeName, dca.OtherSafes AS DecomOtherSafes,
                CAST(dbo.fn_MatchesNamePattern(isf.SafeName, iac.SafeIgnoreMode, iac.SafeIgnoreValue) AS BIT) AS IsInIgnoredSafe,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.vw_selfhosted_internal_safe iis WHERE iis.SafeKey = isf.SafeKey) THEN 1 ELSE 0 END AS BIT) AS IsInInternalSafe
            FROM dbo.fact_account_progress fap
            JOIN dbo.fact_account fa ON fa.AccountKey = fap.AccountKey
            LEFT JOIN web.account_deletion ad ON ad.AccountKey = fa.AccountKey
            LEFT JOIN web.app_user adu ON adu.UserKey = ad.DeletedBy
            LEFT JOIN web.account_risk_score ars   ON ars.AccountKey = fa.AccountKey
            LEFT JOIN web.dim_risk_score_band band ON ars.EffectiveRiskScore BETWEEN band.MinScore AND band.MaxScore
            -- D-186: flagged for deletion (safe or account name pattern).
            OUTER APPLY (SELECT TOP 1 v.InFlaggedSafe, v.NameFlagged, v.SafeName, v.OtherSafes
                         FROM web.vw_decom_account v WHERE v.AccountKey = fa.AccountKey) dca
            LEFT JOIN dbo.dim_safe isf ON isf.SafeKey = fa.SafeKey
            CROSS JOIN web.app_config iac
            WHERE fap.AccountKey = @AccountKey
            """;
        return await connection.QuerySingleOrDefaultAsync<AccountProgressDetail>(sql, new { AccountKey = accountKey });
    }

    /// <summary>
    /// exceptionKey is passed separately from the rest of the request,
    /// rather than read off SaveAccountProgressRequest.ExceptionKey
    /// directly -- the caller (AccountProgressController) validates and
    /// resolves it first (required + must be an Active exception on this
    /// account when status = Risk Accepted / Excluded; cleared otherwise).
    /// </summary>
    public async Task UpdateAsync(long accountKey, SaveAccountProgressRequest request, int? exceptionKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            UPDATE dbo.fact_account_progress
            SET CurrentStageKey = @CurrentStageKey, CurrentStatusKey = @CurrentStatusKey, RiskLevelKey = @RiskLevelKey,
                AccountTypeKey = @AccountTypeKey, SORKey = @SORKey, ApplicationKey = @ApplicationKey, OwnerName = @OwnerName, BusinessUnit = @BusinessUnit,
                TargetRemediationDate = @TargetRemediationDate, ActualCompletionDate = @ActualCompletionDate,
                Notes = @Notes, ExceptionKey = @ExceptionKey, LastUpdated = SYSUTCDATETIME()
            WHERE AccountKey = @AccountKey
            """;
        await connection.ExecuteAsync(sql, new
        {
            AccountKey = accountKey,
            request.CurrentStageKey,
            request.CurrentStatusKey,
            request.RiskLevelKey,
            request.AccountTypeKey,
            request.SORKey,
            request.ApplicationKey,
            request.OwnerName,
            request.BusinessUnit,
            request.TargetRemediationDate,
            request.ActualCompletionDate,
            request.Notes,
            ExceptionKey = exceptionKey
        });
    }

    /// <summary>D-239: whether an application exists (bulk edit validates the one it is given).</summary>
    public async Task<bool> ApplicationExistsAsync(int applicationKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<bool>(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM web.dim_application WHERE ApplicationKey = @applicationKey) THEN 1 ELSE 0 END",
            new { applicationKey });
    }

    /// <summary>D-51 rule 1: Complete requires ActualCompletionDate -- looked up by name, not a hardcoded key.</summary>
    public async Task<string?> GetStatusNameAsync(int statusKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT StatusName FROM dbo.dim_progress_status WHERE StatusKey = @StatusKey", new { StatusKey = statusKey });
    }

    /// <summary>D-183: a status's key by its name (the Risk Exceptions import links with "Risk Accepted / Excluded").</summary>
    public async Task<int?> GetStatusKeyAsync(string statusName)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = @statusName", new { statusName });
    }

    /// <summary>D-51 rule 2: a stage regression (lower StageOrder) requires a Reason.</summary>
    public async Task<int?> GetStageOrderAsync(int stageKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT StageOrder FROM dbo.dim_blueprint_stage WHERE StageKey = @StageKey", new { StageKey = stageKey });
    }

    /// <summary>The current override, if any -- read before a Set call so the controller can log a FieldChange diff.</summary>
    public async Task<(int? OverrideRiskScore, string? OverrideReason)> GetRiskScoreOverrideAsync(long accountKey)
    {
        using var connection = connectionFactory.Create();
        var row = await connection.QuerySingleOrDefaultAsync<(int? OverrideRiskScore, string? OverrideReason)?>(
            "SELECT OverrideRiskScore, OverrideReason FROM web.account_risk_score WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
        return row ?? (null, null);
    }

    /// <summary>
    /// D-101-105 Phase E: an analyst override on top of ComputedRiskScore --
    /// EffectiveRiskScore = COALESCE(OverrideRiskScore, ComputedRiskScore) by
    /// construction, so clearing the override (OverrideRiskScore = NULL)
    /// reverts to the computed value with no extra logic here. Lazily
    /// creates the row (same "insert if missing" convention Phase B/C's
    /// load paths already use) since not every Account has been scored yet.
    /// </summary>
    public async Task SetRiskScoreOverrideAsync(long accountKey, int? overrideRiskScore, string? reason, int setByUserKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(
            "INSERT INTO web.account_risk_score (AccountKey, IsRiskScoreStale) SELECT @AccountKey, 1 WHERE NOT EXISTS (SELECT 1 FROM web.account_risk_score WHERE AccountKey = @AccountKey)",
            new { AccountKey = accountKey }, transaction);

        await connection.ExecuteAsync("""
            UPDATE web.account_risk_score
            SET OverrideRiskScore = @OverrideRiskScore, OverrideReason = @Reason,
                OverrideSetBy = @SetByUserKey, OverrideSetDate = SYSUTCDATETIME()
            WHERE AccountKey = @AccountKey
            """, new { AccountKey = accountKey, OverrideRiskScore = overrideRiskScore, Reason = reason, SetByUserKey = setByUserKey }, transaction);

        transaction.Commit();
    }

    /// <summary>
    /// D-131: a per-account "Recalculate" action on the Account Progress
    /// edit screen's Risk Score tab -- usp_RecalculateRiskScoreForAccount
    /// (Database/30) reuses the same usp_CalculateRiskScore dispatcher the
    /// bulk usp_RecalculateRiskScores does, so this produces the identical
    /// number that a full recalculation would for this one account,
    /// regardless of whether it happened to be marked stale.
    /// </summary>
    public async Task RecalculateForAccountAsync(long accountKey)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("EXEC usp_RecalculateRiskScoreForAccount @AccountKey", new { AccountKey = accountKey });
    }

    /// <summary>D-81: Active application-scoped exceptions covering this account, computed live (web.vw_account_application_exception).</summary>
    public async Task<IReadOnlyList<ApplicationScopedException>> GetApplicationScopedExceptionsAsync(long accountKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT ExceptionID, ApplicationKey, ApplicationName, ReviewDate
            FROM web.vw_account_application_exception
            WHERE AccountKey = @AccountKey
            """;
        var rows = await connection.QueryAsync<ApplicationScopedException>(sql, new { AccountKey = accountKey });
        return rows.AsList();
    }
}
