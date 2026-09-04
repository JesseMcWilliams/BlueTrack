/* ============================================================================
   02_BlueTrack_Test_SyntheticAccountData.sql

   Test-only synthetic data (Design_Testing_Strategy.md) -- NOT part of the
   real environment's numbered-script sequence. Never run this against
   BlueTrack (or any real environment); it only belongs in a disposable
   database like BlueTrackTest.

   RUN THIS AFTER Database/01 through Database/22 (skipping 09) and
   Database/Test/01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql.

   WHAT THIS DOES: seeds a small, entirely synthetic set of safes,
   a platform, accounts, and Account Progress rows -- covering the shapes
   layer 2 (integration) and layer 3 (contract) tests need to exercise
   Account Progress editing (locking, D-51 validation, the Risk Exception
   wiring) and the D-91 auto-advance-to-Onboarded logic, none of which can
   be tested against an empty fact_account/fact_account_progress (which is
   BlueTrackTest's normal state -- it's a schema-only build, no ETL ever
   runs against it).

   Synthetic only, per Design_Testing_Strategy.md's own principle: no row
   here is copied from real Self-Hosted or Privilege Cloud data.

   Guarded throughout -- safe to re-run.
   ============================================================================ */

USE $DatabaseName$;
GO


/* ============================================================================
   1. A synthetic safe, and a second one whose name matches D-91's
      "_Pending" exclusion pattern.
   ============================================================================ */
IF NOT EXISTS (SELECT 1 FROM dim_safe WHERE SafeName = 'TestSafe01')
BEGIN
    INSERT INTO dim_safe (SourceSystemKey, SafeUrlId, SafeName)
    SELECT SourceSystemKey, 'TestSafe01', 'TestSafe01' FROM dim_source_system WHERE SourceSystemCode = 'DISCOVERY';
END

IF NOT EXISTS (SELECT 1 FROM dim_safe WHERE SafeName = 'TestSafe01_Pending')
BEGIN
    INSERT INTO dim_safe (SourceSystemKey, SafeUrlId, SafeName)
    SELECT SourceSystemKey, 'TestSafe01_Pending', 'TestSafe01_Pending' FROM dim_source_system WHERE SourceSystemCode = 'DISCOVERY';
END
GO


/* ============================================================================
   2. A synthetic platform.
   ============================================================================ */
IF NOT EXISTS (SELECT 1 FROM dim_platform WHERE PlatformID = 'TestPlatform01')
BEGIN
    INSERT INTO dim_platform (SourceSystemKey, PlatformID, PlatformName, IsActive, PlatformType)
    SELECT SourceSystemKey, 'TestPlatform01', 'Test Platform', 1, 'Regular' FROM dim_source_system WHERE SourceSystemCode = 'DISCOVERY';
END
GO


/* ============================================================================
   3. Synthetic accounts, each covering a different test scenario:
        TestAccount01 -- in TestSafe01 (not Pending), Discovered/Not Started
                         -> should auto-advance to Onboarded to Vault
        TestAccount02 -- in TestSafe01_Pending, Discovered/Not Started
                         -> should NOT auto-advance (stays Discovered)
        TestAccount03 -- Assessed/Prioritized + In Progress (a manually-
                         curated row) -> auto-advance must never touch it
        TestAccount04 -- Onboarded to Vault + In Progress, used for D-51
                         validation and locking tests (edit/lock flows)
   ============================================================================ */
DECLARE @DiscoveryKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'DISCOVERY');
DECLARE @TestSafeKey INT = (SELECT SafeKey FROM dim_safe WHERE SafeName = 'TestSafe01');
DECLARE @TestPendingSafeKey INT = (SELECT SafeKey FROM dim_safe WHERE SafeName = 'TestSafe01_Pending');
DECLARE @TestPlatformKey INT = (SELECT PlatformKey FROM dim_platform WHERE PlatformID = 'TestPlatform01');

INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, PlatformKey, SafeKey)
SELECT x.SourceSystemKey, v.SourceAccountId, v.AccountName, @TestPlatformKey, v.SafeKey
FROM (VALUES
    ('TestAccount01', 'TestAccount01', @TestSafeKey),
    ('TestAccount02', 'TestAccount02', @TestPendingSafeKey),
    ('TestAccount03', 'TestAccount03', @TestSafeKey),
    ('TestAccount04', 'TestAccount04', @TestSafeKey)
) AS v(SourceAccountId, AccountName, SafeKey)
CROSS APPLY (SELECT @DiscoveryKey AS SourceSystemKey) x
WHERE NOT EXISTS (
    SELECT 1 FROM fact_account fa WHERE fa.SourceSystemKey = @DiscoveryKey AND fa.SourceAccountId = v.SourceAccountId
);
GO


/* ============================================================================
   4. Account Progress rows -- one per synthetic account, per the scenarios
      described above.
   ============================================================================ */
DECLARE @DiscoveredStageKey INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageName = 'Discovered');
DECLARE @AssessedStageKey INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageName = 'Assessed / Prioritized');
DECLARE @OnboardedStageKey INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageName = 'Onboarded to Vault');
DECLARE @NotStartedStatusKey INT = (SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'Not Started');
DECLARE @InProgressStatusKey INT = (SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'In Progress');

INSERT INTO fact_account_progress (AccountKey, CurrentStageKey, CurrentStatusKey)
SELECT fa.AccountKey, v.StageKey, v.StatusKey
FROM fact_account fa
JOIN (VALUES
    ('TestAccount01', @DiscoveredStageKey, @NotStartedStatusKey),
    ('TestAccount02', @DiscoveredStageKey, @NotStartedStatusKey),
    ('TestAccount03', @AssessedStageKey, @InProgressStatusKey),
    ('TestAccount04', @OnboardedStageKey, @InProgressStatusKey)
) AS v(SourceAccountId, StageKey, StatusKey) ON v.SourceAccountId = fa.SourceAccountId
WHERE fa.SourceSystemKey = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'DISCOVERY')
  AND NOT EXISTS (SELECT 1 FROM fact_account_progress fap WHERE fap.AccountKey = fa.AccountKey);
GO

/* ============================================================================
   5. Two synthetic app_user rows for layer-2 locking tests
      (AccountProgressLockRepositoryTests), which need real, stable
      UserKey values to satisfy account_progress_lock.LockedByUserKey's
      FK to web.app_user -- never hardcode a literal UserKey in a test,
      since IDENTITY values depend on whatever else has run first (contract
      tests lazily create their own app_user rows for TestUser.* the first
      time each identity is resolved, in no particular guaranteed order).
   ============================================================================ */
DECLARE @DevFakeAuthProviderKey INT = (SELECT ProviderKey FROM web.identity_provider_config WHERE ProviderType = 'DevFakeAuth');

INSERT INTO web.app_user (ProviderKey, ExternalIdentifier, DisplayName)
SELECT @DevFakeAuthProviderKey, v.ExternalIdentifier, v.ExternalIdentifier
FROM (VALUES ('IntegrationTestUser1'), ('IntegrationTestUser2')) AS v(ExternalIdentifier)
WHERE NOT EXISTS (
    SELECT 1 FROM web.app_user u WHERE u.ProviderKey = @DevFakeAuthProviderKey AND u.ExternalIdentifier = v.ExternalIdentifier
);
GO

PRINT 'Synthetic Account Progress test data seeded (TestAccount01-04).';
