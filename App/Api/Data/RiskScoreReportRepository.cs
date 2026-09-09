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
        ["riskScoreCalculatedDate"] = "ars.RiskScoreCalculatedDate"
    };

    public async Task<IReadOnlyList<RiskScoreReportRow>> GetSummaryListAsync(IReadOnlyList<(string Field, bool Descending)>? sortBy = null)
    {
        using var connection = connectionFactory.Create();

        var sql = $"""
            SELECT
                fa.AccountKey,
                fa.AccountName,
                ars.ComputedRiskScore,
                ars.OverrideRiskScore,
                ars.EffectiveRiskScore,
                ISNULL(ars.IsRiskScoreStale, 1) AS IsRiskScoreStale,
                ars.RiskScoreCalculatedDate
            FROM dbo.fact_account fa
            LEFT JOIN web.account_risk_score ars ON ars.AccountKey = fa.AccountKey
            WHERE fa.IsDeleted = 0
            ORDER BY {BuildOrderByClause(sortBy)}
            """;

        var rows = await connection.QueryAsync<RiskScoreReportRow>(sql);
        return rows.AsList();
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
