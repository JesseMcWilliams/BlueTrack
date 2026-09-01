using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the three Reports sub-pages confirmed by D-56: Overdue/At-Risk
/// Worklist, Stage/Status Funnel Summary, and Reconciliation Review Queue.
/// All three are read-only here -- the Reconciliation Review Queue's
/// confirm/reject actions (gated by ConfirmReconciliation per D-56) aren't
/// wired up yet, matching this scaffold's overall maturity level.
/// </summary>
public sealed class ReportsRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<AccountProgressSummary>> GetOverdueAtRiskListAsync()
    {
        using var connection = connectionFactory.Create();

        const string sql = """
            SELECT
                fa.AccountKey,
                fa.AccountName,
                stg.StageName,
                sts.StatusName,
                rl.RiskLevelName,
                fap.OwnerName,
                fap.TargetRemediationDate,
                fap.ActualCompletionDate
            FROM dbo.fact_account_progress fap
            JOIN dbo.fact_account fa           ON fa.AccountKey = fap.AccountKey
            JOIN dbo.dim_blueprint_stage stg    ON stg.StageKey = fap.CurrentStageKey
            JOIN dbo.dim_progress_status sts     ON sts.StatusKey = fap.CurrentStatusKey
            LEFT JOIN dbo.dim_risk_level rl         ON rl.RiskLevelKey = fap.RiskLevelKey
            WHERE fa.IsDeleted = 0
              AND fap.ActualCompletionDate IS NULL
              AND fap.TargetRemediationDate IS NOT NULL
              AND fap.TargetRemediationDate < CAST(SYSUTCDATETIME() AS DATE)
            ORDER BY fap.TargetRemediationDate ASC
            """;

        var rows = await connection.QueryAsync<AccountProgressSummary>(sql);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<StageStatusFunnelRow>> GetStageStatusFunnelSummaryAsync()
    {
        using var connection = connectionFactory.Create();

        const string sql = """
            SELECT
                stg.StageOrder,
                stg.StageName,
                sts.StatusName,
                COUNT(*) AS AccountCount
            FROM dbo.fact_account_progress fap
            JOIN dbo.fact_account fa        ON fa.AccountKey = fap.AccountKey
            JOIN dbo.dim_blueprint_stage stg ON stg.StageKey = fap.CurrentStageKey
            JOIN dbo.dim_progress_status sts  ON sts.StatusKey = fap.CurrentStatusKey
            WHERE fa.IsDeleted = 0
            GROUP BY stg.StageOrder, stg.StageName, sts.StatusName
            ORDER BY stg.StageOrder, sts.StatusName
            """;

        var rows = await connection.QueryAsync<StageStatusFunnelRow>(sql);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<ReconciliationReviewItem>> GetReconciliationReviewQueueAsync()
    {
        using var connection = connectionFactory.Create();

        const string sql = """
            SELECT * FROM dbo.vw_reconciliation_review_queue
            ORDER BY ReviewPriority, MatchedDate
            """;

        var rows = await connection.QueryAsync<ReconciliationReviewItem>(sql);
        return rows.AsList();
    }
}
