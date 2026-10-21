using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the Reports sub-pages that need no dedicated permission gate:
/// Overdue/At-Risk Worklist, Stage/Status Funnel Summary, Reconciliation
/// Review Queue, Unresolved Entitlement Members, and the KPI Summary
/// (D-143). Permission-gated reports (Risk Score, Risk Exception SoD,
/// Discovered Accounts) each have their own dedicated repository instead.
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
              AND NOT EXISTS (SELECT 1 FROM dbo.vw_linked_feed_account l WHERE l.AccountKey = fa.AccountKey)   -- D-212: a linked feed account counts as its CyberArk account
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

        // Anchored on dim_blueprint_stage CROSS JOIN dim_progress_status
        // (every combination that could exist, not just ones some account
        // currently occupies) with fact_account_progress/fact_account left
        // outer -- a (stage, status) cell with zero accounts still gets a
        // row with AccountCount = 0, instead of silently vanishing from the
        // result set. Found 2026-09-06: the previous inner-join-only query
        // meant an empty stage disappeared entirely from both this report
        // and the Dashboard's "Accounts by Stage" card (which sums these
        // same rows), rather than showing as 0. fa.IsDeleted = 0 moved into
        // the LEFT JOIN's own ON clause (not a WHERE clause, which would
        // silently turn this back into an inner join by discarding the
        // NULL-fa rows this zero-fill depends on) -- a deleted account's
        // fap row just fails to match fa, so COUNT(fa.AccountKey) doesn't
        // count it, without eliminating the (stage, status) row itself.
        const string sql = """
            SELECT
                stg.StageOrder,
                stg.StageName,
                sts.StatusName,
                COUNT(fa.AccountKey) AS AccountCount
            FROM dbo.dim_blueprint_stage stg
            CROSS JOIN dbo.dim_progress_status sts
            LEFT JOIN dbo.fact_account_progress fap ON fap.CurrentStageKey = stg.StageKey AND fap.CurrentStatusKey = sts.StatusKey
            LEFT JOIN dbo.fact_account fa ON fa.AccountKey = fap.AccountKey AND fa.IsDeleted = 0
                AND NOT EXISTS (SELECT 1 FROM dbo.vw_linked_feed_account l WHERE l.AccountKey = fa.AccountKey)   -- D-212
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

    /// <summary>D-232: unresolved rows only (ResolutionMethod null), or with showResolved also those resolved by name, by hand or labelled external.</summary>
    public async Task<IReadOnlyList<UnresolvedEntitlementMember>> GetUnresolvedEntitlementMembersAsync(bool showResolved = false)
    {
        using var connection = connectionFactory.Create();

        const string sql = """
            SELECT v.EntitlementKey, v.SafeKey, v.SafeName, v.MemberType, v.UnresolvedMemberId, v.MemberName, v.ResolutionMethod,
                   v.UserKey, v.GroupKey, v.ResolvedToName, v.ResolutionNote, v.ResolvedByName, v.ResolvedDate,
                   v.MembershipExpirationDate, v.SnapshotDate, ss.SourceSystemCode, ss.SourceSystemName
            FROM dbo.vw_unresolved_entitlement_members v
            JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = v.SourceSystemKey
            WHERE @ShowResolved = 1 OR v.ResolutionMethod IS NULL
            ORDER BY SafeName, MemberType, MemberName, UnresolvedMemberId
            """;

        var rows = await connection.QueryAsync<UnresolvedEntitlementMember>(sql, new { ShowResolved = showResolved });
        return rows.AsList();
    }

    /// <summary>
    /// D-143: five nested counts -- Total >= InScope >= Onboarded >= Managed
    /// >= Compliant -- so the four requested ratios (InScope/Total,
    /// Onboarded/InScope, Managed/Onboarded, Compliant/Managed) can be
    /// computed directly from this one row. "In scope" reuses the existing
    /// Risk Accepted / Excluded status (D-19/D-59) rather than a new
    /// exception type -- an account only leaves scope through that same,
    /// already-enforced accepted-exception mechanism. Onboarded/Managed count
    /// accounts at or past the stage marked with that milestone (D-284; were
    /// fixed positions 3 and 4), and both are additionally restricted to
    /// accounts still in scope so the funnel stays strictly nested; Compliant
    /// requires both the Compliant stage (or later) AND status Complete
    /// specifically (an account can reach it while still Blocked/In Progress
    /// on some final step).
    /// </summary>
    public async Task<KpiSummary> GetKpiSummaryAsync()
    {
        using var connection = connectionFactory.Create();

        const string sql = """
            DECLARE @Onboarded INT = dbo.fn_MilestoneStageOrder(N'Onboarded'), @Managed INT = dbo.fn_MilestoneStageOrder(N'Managed'),
                    @Compliant INT = dbo.fn_MilestoneStageOrder(N'Compliant');
            WITH scope AS (
                SELECT
                    fa.AccountKey,
                    stg.StageOrder,
                    sts.StatusName
                FROM dbo.fact_account fa
                LEFT JOIN dbo.fact_account_progress fap ON fap.AccountKey = fa.AccountKey
                LEFT JOIN dbo.dim_blueprint_stage stg ON stg.StageKey = fap.CurrentStageKey
                LEFT JOIN dbo.dim_progress_status sts ON sts.StatusKey = fap.CurrentStatusKey
                WHERE fa.IsDeleted = 0
                  AND NOT EXISTS (SELECT 1 FROM dbo.vw_linked_feed_account l WHERE l.AccountKey = fa.AccountKey)   -- D-212
            )
            SELECT
                COUNT(*) AS TotalAccounts,
                SUM(CASE WHEN ISNULL(StatusName, '') <> 'Risk Accepted / Excluded' THEN 1 ELSE 0 END) AS InScopeAccounts,
                SUM(CASE WHEN ISNULL(StatusName, '') <> 'Risk Accepted / Excluded' AND StageOrder >= @Onboarded THEN 1 ELSE 0 END) AS OnboardedAccounts,
                SUM(CASE WHEN ISNULL(StatusName, '') <> 'Risk Accepted / Excluded' AND StageOrder >= @Managed THEN 1 ELSE 0 END) AS ManagedAccounts,
                SUM(CASE WHEN StageOrder >= @Compliant AND StatusName = 'Complete' THEN 1 ELSE 0 END) AS CompliantAccounts
            FROM scope
            """;

        var result = await connection.QuerySingleAsync<KpiSummary>(sql);
        return result;
    }
}
