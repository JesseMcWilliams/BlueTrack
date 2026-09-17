/* ============================================================================
   36_BlueTrack_FixAutoAdvanceForDiscoveredAccounts.sql

   RUN THIS AFTER 01-35. Safe to re-run (CREATE OR ALTER -- pure logic, no
   data of its own).

   Real pre-existing gap, found 2026-09-16 while confirming it was safe to
   start inserting DISCOVERY-sourced fact_account rows (the AD Account
   Discovery "accept into onboarding" workflow, Database/35) -- not a bug
   this feature introduces. usp_Load_AccountProgressAutoAdvance
   (03_BlueTrack_ETL_FactLoads.sql) auto-advances any account sitting at
   the default Discovered/Not-Started state to "Onboarded to Vault" unless
   its Safe is a "_Pending" safe -- with NO SourceSystemKey filter at all.
   An account with no Safe whatsoever is explicitly NOT excluded by that
   rule (a NULL SafeKey was always meant to mean "no Pending safe", back
   when every account came from a real CyberArk export and therefore
   always eventually got a real Safe). That reading breaks for a
   DISCOVERY-sourced account: it has no Safe precisely BECAUSE it was never
   actually vaulted, not because it isn't in a Pending one -- without this
   fix, accepting a candidate would get it wrongly auto-promoted to
   "Onboarded to Vault" on the very next nightly Load, despite not being
   onboarded at all.

   This bug was latent, not live, until now: dim_source_system's
   'DISCOVERY' row has existed since 01_BlueTrack_CoreSchema.sql, but
   nothing ever inserted a real fact_account row under it before Database/35
   -- Database/Test's own synthetic DISCOVERY-sourced test accounts are a
   test-only fixture, never run through this procedure in a real load.
   ============================================================================ */

USE $DatabaseName$;
GO

CREATE OR ALTER PROCEDURE usp_Load_AccountProgressAutoAdvance
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @DiscoveredStageKey INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageName = 'Discovered');
    DECLARE @OnboardedStageKey INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageName = 'Onboarded to Vault');
    DECLARE @NotStartedStatusKey INT = (SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'Not Started');
    -- ISNULL guard: if this row were ever somehow missing, @DiscoverySourceSystemKey
    -- would be NULL, and "fa.SourceSystemKey <> NULL" is NULL (never true) for
    -- every row -- silently disabling auto-advance for every account, a far
    -- worse regression than the one this script fixes. Falling back to -1
    -- (never a real SourceSystemKey) instead makes a missing row a no-op for
    -- this new condition, not a silent full outage.
    DECLARE @DiscoverySourceSystemKey INT = ISNULL((SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'DISCOVERY'), -1);

    UPDATE fap
    SET fap.CurrentStageKey = @OnboardedStageKey,
        fap.LastUpdated = SYSUTCDATETIME()
    FROM fact_account_progress fap
    JOIN fact_account fa ON fa.AccountKey = fap.AccountKey
    LEFT JOIN dim_safe ds ON ds.SafeKey = fa.SafeKey
    WHERE fap.CurrentStageKey = @DiscoveredStageKey
      AND fap.CurrentStatusKey = @NotStartedStatusKey
      AND fa.IsDeleted = 0
      AND fa.SourceSystemKey <> @DiscoverySourceSystemKey
      AND (ds.SafeKey IS NULL OR ds.SafeName NOT LIKE '%[_]Pending%');
END
GO

PRINT '36_BlueTrack_FixAutoAdvanceForDiscoveredAccounts.sql complete.';
