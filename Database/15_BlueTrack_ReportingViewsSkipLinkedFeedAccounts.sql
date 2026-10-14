/* ============================================================================
   15_BlueTrack_ReportingViewsSkipLinkedFeedAccounts.sql

   RUN THIS AFTER 01-14. Safe to re-run (CREATE OR ALTER only).

   D-214: a data feed account linked to a CyberArk account (D-212,
   dbo.vw_linked_feed_account) is the same account as the CyberArk one, and
   the app already counts it once. The SQL reporting objects outside the app
   now do the same:
   - vw_export_account_progress (the CSV export source),
   - vw_powerbi_accounts (Power BI's account table),
   - usp_Load_FactAccountProgressHistory (the daily snapshot behind trend
     visuals).
   Each is the 02_BlueTrack_Baseline_EtlLoads.sql definition plus one
   NOT EXISTS against vw_linked_feed_account.
   ============================================================================ */

USE $DatabaseName$;
GO

CREATE OR ALTER VIEW vw_export_account_progress AS
SELECT
    ss.SourceSystemName,
    fa.SourceAccountId,
    fa.AccountName,
    fa.UserName,
    fa.Address,
    dp.PlatformName,
    ds.SafeName,
    at.AccountTypeName,
    sor.SORName               AS SourceOfRecord,
    stg.StageName            AS CurrentStage,
    stat.StatusName          AS CurrentStatus,
    rl.RiskLevelName          AS RiskLevel,
    fap.OwnerName,
    fap.BusinessUnit,
    fap.TargetRemediationDate,
    fap.ActualCompletionDate,
    fap.LastUpdated,
    fap.Notes
FROM fact_account_progress fap
JOIN fact_account fa        ON fa.AccountKey = fap.AccountKey
JOIN dim_source_system ss   ON ss.SourceSystemKey = fa.SourceSystemKey
LEFT JOIN dim_platform dp   ON dp.PlatformKey = fa.PlatformKey
LEFT JOIN dim_safe ds       ON ds.SafeKey = fa.SafeKey
LEFT JOIN dim_account_type at ON at.AccountTypeKey = fap.AccountTypeKey
LEFT JOIN dim_source_of_record sor ON sor.SORKey = fap.SORKey
LEFT JOIN dim_risk_level rl ON rl.RiskLevelKey = fap.RiskLevelKey
JOIN dim_blueprint_stage stg ON stg.StageKey = fap.CurrentStageKey
JOIN dim_progress_status stat ON stat.StatusKey = fap.CurrentStatusKey
WHERE NOT EXISTS (SELECT 1 FROM dbo.vw_linked_feed_account l WHERE l.AccountKey = fa.AccountKey);
GO

CREATE OR ALTER VIEW vw_powerbi_accounts AS
SELECT
    fa.AccountKey,
    fa.SourceSystemKey,
    fa.SourceAccountId,
    fa.AccountName,
    fa.Address,
    fa.UserName,
    fa.PlatformKey,
    fa.SafeKey,
    fa.SecretType,
    fa.AutoManaged,
    fa.CPMStatus,
    fa.LastCPMModifiedDate,
    fa.LastReconciledDate,
    fa.LastVerifiedDate,
    fa.IsDeleted,
    fa.CreatedDate,
    fap.ProgressKey,
    fap.CurrentStageKey,
    fap.CurrentStatusKey,
    fap.AccountTypeKey,
    fap.SORKey,
    fap.RiskLevelKey,
    fap.OwnerName,
    fap.BusinessUnit,
    fap.TargetRemediationDate,
    fap.ActualCompletionDate,
    fap.LastUpdated AS ProgressLastUpdated,
    -- surfaces a plain flag for a common Power BI card/KPI without needing a DAX measure
    CASE WHEN fap.ProgressKey IS NULL THEN 1 ELSE 0 END AS IsUntrackedInBlueprint,
    -- surfaces overdue remediations directly for a simple visual filter
    CASE WHEN fap.TargetRemediationDate < CAST(GETDATE() AS DATE)
              AND fap.ActualCompletionDate IS NULL
         THEN 1 ELSE 0 END AS IsOverdue
FROM fact_account fa
LEFT JOIN fact_account_progress fap ON fap.AccountKey = fa.AccountKey
WHERE NOT EXISTS (SELECT 1 FROM dbo.vw_linked_feed_account l WHERE l.AccountKey = fa.AccountKey);
GO

CREATE OR ALTER PROCEDURE usp_Load_FactAccountProgressHistory
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @TodayKey INT = CAST(FORMAT(GETDATE(), 'yyyyMMdd') AS INT);

    IF NOT EXISTS (SELECT 1 FROM dim_date WHERE DateKey = @TodayKey)
    BEGIN
        RAISERROR('dim_date has no row for today -- extend the date range and re-run the dim_date population script before scheduling this proc.', 16, 1);
        RETURN;
    END

    -- avoid duplicate snapshots if this is run more than once on the same day
    DELETE FROM fact_account_progress_history WHERE SnapshotDateKey = @TodayKey;

    INSERT INTO fact_account_progress_history (SnapshotDateKey, AccountKey, StageKey, StatusKey, RiskLevelKey)
    SELECT @TodayKey, fap.AccountKey, fap.CurrentStageKey, fap.CurrentStatusKey, fap.RiskLevelKey
    FROM fact_account_progress fap
    JOIN fact_account fa ON fa.AccountKey = fap.AccountKey
    WHERE fa.IsDeleted = 0
      AND NOT EXISTS (SELECT 1 FROM dbo.vw_linked_feed_account l WHERE l.AccountKey = fa.AccountKey);   -- D-214
END
GO
