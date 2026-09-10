using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>Backs the new Risk Score report (Design_Risk_Scoring.md, D-101-105, Phase E).</summary>
public sealed class RiskScoreReportRepository(IDbConnectionFactory connectionFactory)
{
    private static readonly IReadOnlyDictionary<string, string> SortableColumns = new Dictionary<string, string>
    {
        ["accountName"] = "fa.AccountName",
        ["computedRiskScore"] = "ars.ComputedRiskScore",
        ["overrideRiskScore"] = "ars.OverrideRiskScore",
        ["effectiveRiskScore"] = "ars.EffectiveRiskScore",
        ["isRiskScoreStale"] = "ars.IsRiskScoreStale",
        ["riskScoreCalculatedDate"] = "ars.RiskScoreCalculatedDate",
        ["riskScoreBandName"] = "band.RiskOrder"
    };

    /// <summary>D-124 Phase 3: page/pageSize add SQL Server OFFSET/FETCH paging after the ORDER BY. This report takes no filter params, so GetFilteredCountAsync below is always equal to GetTotalCountAsync, but paging still applies.</summary>
    public async Task<IReadOnlyList<RiskScoreReportRow>> GetSummaryListAsync(
        IReadOnlyList<(string Field, bool Descending)>? sortBy = null,
        int? page = null,
        int? pageSize = null)
    {
        using var connection = connectionFactory.Create();
        var (normalizedPage, normalizedPageSize) = PagingParams.Normalize(page, pageSize);

        var sql = $"""
            SELECT
                fa.AccountKey,
                fa.AccountName,
                ars.ComputedRiskScore,
                ars.OverrideRiskScore,
                ars.EffectiveRiskScore,
                ISNULL(ars.IsRiskScoreStale, 1) AS IsRiskScoreStale,
                ars.RiskScoreCalculatedDate,
                band.BandName AS RiskScoreBandName
            FROM dbo.fact_account fa
            LEFT JOIN web.account_risk_score ars ON ars.AccountKey = fa.AccountKey
            LEFT JOIN web.dim_risk_score_band band ON ars.EffectiveRiskScore BETWEEN band.MinScore AND band.MaxScore
            WHERE fa.IsDeleted = 0
            ORDER BY {BuildOrderByClause(sortBy)}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """;

        var rows = await connection.QueryAsync<RiskScoreReportRow>(sql, new
        {
            Offset = PagingParams.Offset(normalizedPage, normalizedPageSize),
            PageSize = normalizedPageSize
        });
        return rows.AsList();
    }

    /// <summary>D-121: the grand total row count under the same base "active" condition (fa.IsDeleted = 0) -- backs the X-Total-Count response header. This report takes no filter params, so this is always equal to the body's own row count, but it establishes the same header on all six pages consistently.</summary>
    public async Task<int> GetTotalCountAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.fact_account fa WHERE fa.IsDeleted = 0");
    }

    /// <summary>D-124 Phase 3: backs the new X-Filtered-Count header. This report takes no filter params, so it's always equal to GetTotalCountAsync -- kept as its own method for consistency with the other five paginated endpoints, all of which have a real filter/unfiltered distinction.</summary>
    public async Task<int> GetFilteredCountAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.fact_account fa WHERE fa.IsDeleted = 0");
    }

    private static string BuildOrderByClause(IReadOnlyList<(string Field, bool Descending)>? sortBy)
    {
        if (sortBy is not { Count: > 0 })
        {
            return "ars.EffectiveRiskScore DESC";
        }

        var clauses = sortBy
            .Where(s => SortableColumns.ContainsKey(s.Field))
            .Select(s => $"{SortableColumns[s.Field]} {(s.Descending ? "DESC" : "ASC")}")
            .ToList();

        return clauses.Count > 0 ? string.Join(", ", clauses) : "ars.EffectiveRiskScore DESC";
    }

    /// <summary>
    /// The drill-down behind a report row -- every real Target/Access Group
    /// currently contributing to this Account's reachable-value set, per
    /// web.ufn_ReachableRiskValues (24_BlueTrack_RiskScoringReportDrilldown.sql
    /// added the EntityType/EntityKey/EntityName columns this needs).
    /// </summary>
    public async Task<IReadOnlyList<RiskScoreContributor>> GetContributorsAsync(long accountKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT EntityType, EntityKey, EntityName, RiskValue
            FROM web.ufn_ReachableRiskValues('Account', @AccountKey)
            ORDER BY RiskValue DESC
            """;
        var rows = await connection.QueryAsync<RiskScoreContributor>(sql, new { AccountKey = accountKey });
        return rows.AsList();
    }

    /// <summary>
    /// D-119 Phase D/E: usp_RecalculateRiskScores also runs automatically
    /// inside usp_RunFullLoad, but an admin/analyst may want an immediate
    /// refresh after editing a Target/Access Group rather than waiting for
    /// the next scheduled load -- this is that manual trigger.
    /// </summary>
    public async Task RecalculateAllAsync()
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("EXEC usp_RecalculateRiskScores", commandTimeout: 300);
    }
}
