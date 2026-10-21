/* ============================================================================
   46_BlueTrack_PowerBiCpmActionColumns.sql

   RUN THIS AFTER 01-45. Safe to re-run.

   D-277 (asked 2026-10-21): vw_powerbi_accounts also gives the account
   columns script 45 added for Self-Hosted Direct: CreatedTime and the last
   CPM action, its result and its time. As in
   15_BlueTrack_ReportingViewsSkipLinkedFeedAccounts.sql, with the four
   columns added at the end so existing reports keep their columns.
   ============================================================================ */

USE $DatabaseName$;
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
         THEN 1 ELSE 0 END AS IsOverdue,
    -- D-277: Self-Hosted Direct only (NULL otherwise); times are UTC.
    fa.CreatedTime,
    fa.LastCPMAction,
    fa.LastCPMActionResult,
    fa.LastCPMActionTime
FROM fact_account fa
LEFT JOIN fact_account_progress fap ON fap.AccountKey = fa.AccountKey
WHERE NOT EXISTS (SELECT 1 FROM dbo.vw_linked_feed_account l WHERE l.AccountKey = fa.AccountKey);
GO

PRINT '46_BlueTrack_PowerBiCpmActionColumns.sql complete.';
GO
