/* ============================================================================
   04_BlueTrack_ETL_ReportingViews.sql

   Split 2026-09-05 out of the former 02_BlueTrack_ETL_LoadProcedures.sql
   (see Database/README.md for the full script-numbering rationale). This
   file holds only the reporting views -- the dimension-table loaders moved
   to 02_BlueTrack_ETL_DimensionLoads.sql and the fact-table loaders moved
   to 03_BlueTrack_ETL_FactLoads.sql. Object names, bodies, and logic are
   byte-for-byte unchanged from the original file; only the file layout
   changed.

   Depends on: 01_BlueTrack_CoreSchema.sql, 02_BlueTrack_ETL_DimensionLoads.sql,
   03_BlueTrack_ETL_FactLoads.sql (these views query fact_, dim_, and
   bridge_ tables that must exist, though they don't require those tables
   to be populated yet to be created).
   ============================================================================ */

USE $DatabaseName$;
GO

/* ============================================================================
   REPORTING VIEW -- effective safe access, direct + group-expanded
   One row per (Safe, User) pair who can access it, whether granted
   directly or through a Group entitlement expanded via
   bridge_group_membership. A user with both direct and group-based access
   to the same safe intentionally appears twice (once per AccessPath) --
   that's informative, not a duplicate to dedupe away. This is the natural
   source for "how many actual known CyberArk users touch this safe/
   application" questions, e.g. for license-count estimation.
   ============================================================================ */
CREATE OR ALTER VIEW vw_effective_safe_access AS
SELECT fse.SafeKey, u.UserKey, 'Direct' AS AccessPath, fse.EntitlementKey AS SourceEntitlementKey
FROM fact_safe_entitlement fse
JOIN dim_user u ON u.UserKey = fse.UserKey
WHERE fse.MemberType = 'User'
UNION ALL
SELECT fse.SafeKey, bgm.UserKey, 'Via Group: ' + g.GroupName, fse.EntitlementKey
FROM fact_safe_entitlement fse
JOIN dim_group g ON g.GroupKey = fse.GroupKey
JOIN bridge_group_membership bgm ON bgm.GroupKey = g.GroupKey
WHERE fse.MemberType = 'Group';
GO
/* ============================================================================
   REPORTING VIEW -- CSV export source
   Point your CSV export (SSMS "Results to File", bcp, or an app-level
   export button) at this view rather than the raw fact table.
   ============================================================================ */
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
JOIN dim_progress_status stat ON stat.StatusKey = fap.CurrentStatusKey;
GO
/* ============================================================================
   REPORTING VIEW -- platform mapping review queue
   Surfaces every Platform in use, whether it's been mapped in
   platform_account_type_map yet, and a sample Address from an account on
   that Platform as a manual sanity-check hint. Address is deliberately
   NOT used anywhere in the automated ETL logic -- it's too unreliable as a
   primary signal (a hostname doesn't reliably tell you what controls the
   identity logging into it) -- but it's a useful secondary confirmation
   for a human reviewer curating platform_account_type_map (e.g. an FQDN
   with a domain suffix supporting an 'Active Directory' classification).
   Point whoever maintains platform_account_type_map at this view; it does
   not write to any table.
   ============================================================================ */
CREATE OR ALTER VIEW vw_review_platform_sor_accounttype AS
SELECT
    dp.PlatformKey,
    ss.SourceSystemName,
    dp.PlatformID,
    dp.PlatformName,
    pm.AccountTypeKey,
    at.AccountTypeName,
    pm.SORKey,
    sor.SORName                AS SourceOfRecord,
    CASE WHEN pm.PlatformKey IS NULL THEN 1 ELSE 0 END AS IsUnmapped,
    accountCounts.AccountCount,
    sampleAccount.Address      AS SampleAddress,
    sampleAccount.AccountName  AS SampleAccountName
FROM dim_platform dp
JOIN dim_source_system ss ON ss.SourceSystemKey = dp.SourceSystemKey
LEFT JOIN platform_account_type_map pm ON pm.PlatformKey = dp.PlatformKey
LEFT JOIN dim_account_type at ON at.AccountTypeKey = pm.AccountTypeKey
LEFT JOIN dim_source_of_record sor ON sor.SORKey = pm.SORKey
OUTER APPLY (
    SELECT COUNT(*) AS AccountCount
    FROM fact_account fa
    WHERE fa.PlatformKey = dp.PlatformKey AND fa.IsDeleted = 0
) accountCounts
OUTER APPLY (
    SELECT TOP 1 fa.Address, fa.AccountName
    FROM fact_account fa
    WHERE fa.PlatformKey = dp.PlatformKey AND fa.IsDeleted = 0 AND fa.Address IS NOT NULL
    ORDER BY fa.AccountKey
) sampleAccount;
GO

PRINT '04_BlueTrack_ETL_ReportingViews.sql complete.';
GO
