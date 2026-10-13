/* ============================================================================
   13_BlueTrack_AccountInventoryFeed.sql

   RUN THIS AFTER 01-12. Guarded -- safe to re-run.

   Data Sources phase 4a (Planning_Data-Sources.md, D-212): accounts from
   other systems, imported by a data feed.

   - web.data_feed gets SourceSystemKey (the system an AccountInventory
     feed's accounts come from; dim_source_system) and the AccountInventory
     feed type.
   - usp_Load_AccountProgressAutoAdvance: only CyberArk accounts (PRIVCLOUD,
     SELFHOSTED) auto-advance from Discovered to Onboarded to Vault. Before,
     every source except DISCOVERY did, and a feed account has no safe, so it
     would have been advanced the first night. A feed account moves only
     when it's linked to a CyberArk account (phase 4b).
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.data_feed', 'SourceSystemKey') IS NULL
BEGIN
    ALTER TABLE web.data_feed ADD SourceSystemKey INT NULL
        CONSTRAINT FK_data_feed_SourceSystem REFERENCES dbo.dim_source_system(SourceSystemKey);
END
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_data_feed_FeedType'
           AND definition NOT LIKE '%AccountInventory%')
BEGIN
    ALTER TABLE web.data_feed DROP CONSTRAINT CK_data_feed_FeedType;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_data_feed_FeedType')
BEGIN
    ALTER TABLE web.data_feed ADD CONSTRAINT CK_data_feed_FeedType CHECK (FeedType IN (
        'TargetInventory', 'AccessGroupInventory', 'AccessGroupTargetMap',
        'AccountAccessGroupMembership', 'AccountTargetMap', 'Applications', 'SafeAssignments',
        'AccountInventory'));
END
GO

CREATE OR ALTER PROCEDURE usp_Load_AccountProgressAutoAdvance
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @DiscoveredStageKey INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageName = 'Discovered');
    DECLARE @OnboardedStageKey INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageName = 'Onboarded to Vault');
    DECLARE @NotStartedStatusKey INT = (SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'Not Started');

    -- D-212: CyberArk accounts only. DISCOVERY (AD Account Discovery) and every
    -- data feed source are left alone; a feed account moves when it's linked.
    UPDATE fap
    SET fap.CurrentStageKey = @OnboardedStageKey,
        fap.LastUpdated = SYSUTCDATETIME()
    FROM fact_account_progress fap
    JOIN fact_account fa ON fa.AccountKey = fap.AccountKey
    JOIN dim_source_system ss ON ss.SourceSystemKey = fa.SourceSystemKey
    LEFT JOIN dim_safe ds ON ds.SafeKey = fa.SafeKey
    WHERE fap.CurrentStageKey = @DiscoveredStageKey
      AND fap.CurrentStatusKey = @NotStartedStatusKey
      AND fa.IsDeleted = 0
      AND ss.SourceSystemCode IN ('PRIVCLOUD', 'SELFHOSTED')
      AND (ds.SafeKey IS NULL OR ds.SafeName NOT LIKE '%[_]Pending%');
END
GO
