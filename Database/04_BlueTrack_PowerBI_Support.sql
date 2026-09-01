/* ============================================================================
   04_BlueTrack_PowerBI_Support.sql

   RUN THIS FILE FOURTH AND LAST, after 01_BlueTrack_CreateDatabase_Schema.sql,
   02_BlueTrack_ETL_LoadProcedures.sql, and
   03_BlueTrack_AccountReconciliation.sql.
   Includes a USE BlueTrack; statement below, so you don't need to
   set the database context manually before running this.

   Power BI Support Objects + Full-Load Orchestration
   Target: SQL Server / Azure SQL

   Contents:
     1. dim_date population (empty shell table created in file 01)
     2. vw_powerbi_accounts     -- one row per account, joins fact_account +
        fact_account_progress, keeps FK columns (not resolved names) so
        Power BI builds its own relationships to the dimension tables
     3. vw_powerbi_entitlements -- flattened entitlement + permission grain
        for access/permission analysis
     4. usp_Load_FactAccountProgressHistory -- populates the daily/weekly
        snapshot table Power BI needs for trend-over-time visuals
     5. usp_RunFullLoad -- the orchestration procedure that runs every load
        step from files 02-04 in dependency order. It's defined here, last,
        specifically because this is the first point in the four-file
        sequence where every procedure it calls actually exists.

   *** BATCHING NOTE ***
   CREATE OR ALTER VIEW and CREATE OR ALTER PROCEDURE must each be the only
   statement in their batch -- each is followed by GO below. The dim_date
   population script also ends with GO to keep it isolated from what
   follows, though that's for readability here rather than a hard
   requirement (a recursive CTE + INSERT is not restricted to its own batch).
   ============================================================================ */

USE BlueTrack;
GO

/* ----------------------------------------------------------------------------
   1. dim_date population
   Adjust @StartDate/@EndDate to your engagement's actual timeline -- this
   defaults to a broad range so it doesn't need re-running as the project
   goes on. Safe to run once; re-running only adds nothing (INSERT guarded
   by NOT EXISTS).
---------------------------------------------------------------------------- */
DECLARE @StartDate DATE = '2026-01-01';
DECLARE @EndDate   DATE = '2028-12-31';

;WITH DateSeries AS (
    SELECT @StartDate AS FullDate
    UNION ALL
    SELECT DATEADD(DAY, 1, FullDate) FROM DateSeries WHERE FullDate < @EndDate
)
INSERT INTO dim_date (DateKey, FullDate, Year, Quarter, Month, MonthName, Week, DayOfMonth, DayName)
SELECT
    CAST(FORMAT(FullDate, 'yyyyMMdd') AS INT),
    FullDate,
    YEAR(FullDate),
    DATEPART(QUARTER, FullDate),
    MONTH(FullDate),
    DATENAME(MONTH, FullDate),
    DATEPART(WEEK, FullDate),
    DAY(FullDate),
    DATENAME(WEEKDAY, FullDate)
FROM DateSeries
WHERE CAST(FORMAT(FullDate, 'yyyyMMdd') AS INT) NOT IN (SELECT DateKey FROM dim_date)
OPTION (MAXRECURSION 0);
GO


/* ----------------------------------------------------------------------------
   2. vw_powerbi_accounts
   One row per account (current state). Deliberately keeps foreign keys
   (PlatformKey, SafeKey, CurrentStageKey, etc.) rather than resolving them
   to names here -- resolving names in the view would bypass Power BI's own
   relationship/filter engine and break slicer interactions. Import this as
   one table and build relationships to the dimension tables in Power BI's
   Model view.

   Accounts with no fact_account_progress row yet (LEFT JOIN) show as
   NULL stage/status -- decide in Power BI whether to treat that as
   "Not Started" via a calculated column, or surface it as a genuine
   "not yet tracked" data-quality signal. I'd lean toward the latter: an
   account that exists in the vault but has no progress row is worth
   knowing about, not silently defaulting.
---------------------------------------------------------------------------- */
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
LEFT JOIN fact_account_progress fap ON fap.AccountKey = fa.AccountKey;
GO


/* ----------------------------------------------------------------------------
   3. vw_powerbi_entitlements
   Grain: one row per (safe, member, permission). Keeps FKs for the same
   reason as above. This is the table to relate to dim_safe, dim_user,
   dim_group, and dim_permission for access-review visuals ("who can
   retrieve passwords from which safes", entitlement counts by safe, etc.)
---------------------------------------------------------------------------- */
CREATE OR ALTER VIEW vw_powerbi_entitlements AS
SELECT
    bep.EntitlementKey,
    fse.SafeKey,
    fse.MemberType,
    fse.UserKey,
    fse.GroupKey,
    fse.SnapshotDate,
    bep.PermissionKey,
    bep.IsGranted
FROM fact_safe_entitlement fse
JOIN bridge_entitlement_permission bep ON bep.EntitlementKey = fse.EntitlementKey;
GO


/* ----------------------------------------------------------------------------
   4. usp_Load_FactAccountProgressHistory
   Run this on a schedule (SQL Agent job, nightly or weekly -- match your
   Blueprint check-in cadence) to append one snapshot row per account per
   run. This is what makes trend-over-time visuals possible in Power BI;
   fact_account_progress alone only ever shows current state.
---------------------------------------------------------------------------- */
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
    WHERE fa.IsDeleted = 0;
END
GO


/* ============================================================================
   5. ORCHESTRATION -- runs the full load, in dependency order, inside one
      transaction. This is the procedure to schedule (SQL Agent job) for
      routine reloads once all four setup files have been run:
        EXEC usp_RunFullLoad;
   ============================================================================ */
CREATE OR ALTER PROCEDURE usp_RunFullLoad
AS
BEGIN
    SET NOCOUNT ON;
    BEGIN TRY
        BEGIN TRANSACTION;

        EXEC usp_Load_DimLocation;
        EXEC usp_Load_DimPlatform;
        EXEC usp_Load_DimSafe;
        EXEC usp_Load_DimUser;
        EXEC usp_Load_DimGroup;
        EXEC usp_Load_GroupMembership;
        EXEC usp_Load_FactAccount;
        EXEC usp_Load_FactAccountProgress;
        EXEC usp_Load_FactSafeEntitlement;
        EXEC usp_Load_AccountReconciliation;
        EXEC usp_Load_FactAccountProgressHistory;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        THROW;
    END CATCH
END
GO

PRINT 'Power BI support objects and full-load orchestration created successfully.';
PRINT 'Setup complete. Run EXEC usp_RunFullLoad; after loading the stg_* tables from your source exports.';
