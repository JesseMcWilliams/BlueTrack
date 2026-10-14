/* ============================================================================
   21_BlueTrack_SnapshotAndSiteUrl.sql

   RUN THIS AFTER 01-20. Guarded -- safe to re-run.

   D-224, decided by the user on 2026-10-13: two follow-ups.

   1. The daily snapshot is taken even when the Import or the Load fails
      (Design_Data-Sources.md). Until now usp_Load_FactAccountProgressHistory
      ran only inside usp_RunFullLoad's transaction, so a failed night left a
      gap in Reports > Trends (D-221). On a failed night the snapshot records
      the current, unchanged state.

     dbo.usp_RunDailySnapshot       Runs usp_Load_FactAccountProgressHistory
                                    (still DELETE today, then INSERT, so a
                                    second run the same day replaces the
                                    first) in its own transaction, and
                                    records the run in dbo.etl_run_log as
                                    StepName 'Snapshot' (D-222), so a failed
                                    snapshot is emailed like a failed Import
                                    or Load.
     dbo.usp_RunNightlyImportLoad   What the nightly SQL Agent job runs from
                                    now on (Database/Manual/04, one step):
                                    usp_Import_Configured, then (only if it
                                    succeeded) usp_RunFullLoad, then the
                                    snapshot whatever happened. A failure is
                                    re-raised after the snapshot, so the job
                                    still reports it.
     usp_RunFullLoad                Redefined: the snapshot leaves the load's
                                    transaction. New @TakeSnapshot (default
                                    1) runs usp_RunDailySnapshot after the
                                    load, succeeded or failed, so a job
                                    created before this script (EXEC
                                    usp_RunFullLoad;) and a run by hand keep
                                    snapshotting; usp_RunNightlyImportLoad
                                    passes 0 and takes the snapshot itself.
                                    Keeps the etl_run_log writes (script 19).
     CK_etl_run_log_StepName        Allows 'Snapshot'.

   2. web.app_config.SiteUrl        The address people open BlueTrack at
                                    (e.g. https://bluetrack.company.com),
                                    set on Global Application Configuration
                                    or, when empty, by the installer's
                                    Db.SiteUrl step. Emails link to pages
                                    under it; NULL keeps the page names only.
   ============================================================================ */

USE $DatabaseName$;
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints
           WHERE name = 'CK_etl_run_log_StepName' AND parent_object_id = OBJECT_ID('dbo.etl_run_log')
             AND definition NOT LIKE '%Snapshot%')
    ALTER TABLE dbo.etl_run_log DROP CONSTRAINT CK_etl_run_log_StepName;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_etl_run_log_StepName' AND parent_object_id = OBJECT_ID('dbo.etl_run_log'))
    ALTER TABLE dbo.etl_run_log ADD CONSTRAINT CK_etl_run_log_StepName CHECK (StepName IN ('Import', 'Load', 'Snapshot'));
GO

IF COL_LENGTH('web.app_config', 'SiteUrl') IS NULL
    ALTER TABLE web.app_config ADD SiteUrl NVARCHAR(500) NULL;
GO

/* usp_RunDailySnapshot: the daily snapshot (usp_Load_FactAccountProgressHistory,
   as in 15_BlueTrack_ReportingViewsSkipLinkedFeedAccounts.sql) with its own
   etl_run_log row. */
CREATE OR ALTER PROCEDURE dbo.usp_RunDailySnapshot
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @EtlRunKey BIGINT;
    INSERT INTO dbo.etl_run_log (StepName) VALUES ('Snapshot');
    SET @EtlRunKey = SCOPE_IDENTITY();

    BEGIN TRY
        -- One transaction, so a failure never leaves today's snapshot deleted but not replaced.
        BEGIN TRANSACTION;
        EXEC dbo.usp_Load_FactAccountProgressHistory;
        COMMIT TRANSACTION;

        UPDATE dbo.etl_run_log SET Status = 'Succeeded', CompletedAt = SYSUTCDATETIME() WHERE EtlRunKey = @EtlRunKey;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        UPDATE dbo.etl_run_log
        SET Status = 'Failed', CompletedAt = SYSUTCDATETIME(), ErrorMessage = LEFT(ERROR_MESSAGE(), 2000),
            ErrorProcedure = ERROR_PROCEDURE(), ErrorLine = ERROR_LINE()
        WHERE EtlRunKey = @EtlRunKey;
        THROW;
    END CATCH
END
GO

/* usp_RunFullLoad: as in 19_BlueTrack_MoreNotifications.sql, but the
   snapshot runs after the load's transaction, succeeded or failed, unless
   @TakeSnapshot = 0. */
CREATE OR ALTER PROCEDURE usp_RunFullLoad
    @TakeSnapshot BIT = 1
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @EtlRunKey BIGINT;
    INSERT INTO dbo.etl_run_log (StepName) VALUES ('Load');
    SET @EtlRunKey = SCOPE_IDENTITY();

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
        EXEC usp_Load_AccountProgressAutoAdvance;
        EXEC usp_Load_FactSafeEntitlement;
        EXEC usp_Load_AccountReconciliation;
        -- D-224: usp_Load_FactAccountProgressHistory moved out, to usp_RunDailySnapshot below.
        EXEC usp_DeriveTargetsFromAccountAddress;   -- D-123 (51_)
        EXEC usp_DeriveAccountTargetMap_FromPendingSafes;
        EXEC usp_DeriveAccountTargetMap_FromAddress;    -- D-193 (51_)
        EXEC usp_RecalculateRiskScores;

        COMMIT TRANSACTION;

        UPDATE dbo.etl_run_log SET Status = 'Succeeded', CompletedAt = SYSUTCDATETIME() WHERE EtlRunKey = @EtlRunKey;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        UPDATE dbo.etl_run_log
        SET Status = 'Failed', CompletedAt = SYSUTCDATETIME(), ErrorMessage = LEFT(ERROR_MESSAGE(), 2000),
            ErrorProcedure = ERROR_PROCEDURE(), ErrorLine = ERROR_LINE()
        WHERE EtlRunKey = @EtlRunKey;

        -- D-224: snapshot the unchanged state; a snapshot failure is in its own etl_run_log row.
        IF @TakeSnapshot = 1
        BEGIN
            BEGIN TRY
                EXEC dbo.usp_RunDailySnapshot;
            END TRY
            BEGIN CATCH
                IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
            END CATCH
        END;

        THROW;
    END CATCH

    IF @TakeSnapshot = 1
        EXEC dbo.usp_RunDailySnapshot;
END
GO

/* usp_RunNightlyImportLoad: the nightly job's one step (D-224). */
CREATE OR ALTER PROCEDURE dbo.usp_RunNightlyImportLoad
AS
BEGIN
    SET NOCOUNT ON;

    BEGIN TRY
        EXEC dbo.usp_Import_Configured;
        -- As before: a failed Import stops here, so the Load never runs on a partial import.
        EXEC dbo.usp_RunFullLoad @TakeSnapshot = 0;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;

        -- The snapshot of the unchanged state; its own failure is in its etl_run_log row.
        BEGIN TRY
            EXEC dbo.usp_RunDailySnapshot;
        END TRY
        BEGIN CATCH
            IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        END CATCH;

        -- The Import or Load error, so the SQL Agent job reports the failure.
        THROW;
    END CATCH

    EXEC dbo.usp_RunDailySnapshot;
END
GO
