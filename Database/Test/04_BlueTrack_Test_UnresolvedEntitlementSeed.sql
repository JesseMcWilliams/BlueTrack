/* ============================================================================
   04_BlueTrack_Test_UnresolvedEntitlementSeed.sql

   Test-only synthetic data (Design_Testing-Strategy.md) -- NOT part of the
   real environment's numbered-script sequence. Never run this against
   BlueTrack (or any real environment).

   RUN THIS AFTER 03_BlueTrack_Test_TrendsHistorySeed.sql (and after
   24_BlueTrack_EntitlementMemberResolution.sql).

   WHAT THIS DOES (D-232): one unresolved entitlement for the Playwright
   Unresolved Entitlement Members spec, as a load before D-232 would have
   left it: on the Privilege Cloud safe TestSafe_Entitlements, a User member
   with ID e2e-0000-unresolved-guid and name E2E Unresolved Member, which no
   user has (so Undo leaves it unresolved, not matched by name), and the
   Privilege Cloud user e2e.resolve.target (E2E-U1) to link it to. The spec
   resolves it and undoes it, so it ends as it started. No load runs in the
   test suites outside a rolled-back transaction, so the row stays.

   Guarded -- safe to re-run.
   ============================================================================ */

USE $DatabaseName$;
GO

DECLARE @src INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');

IF NOT EXISTS (SELECT 1 FROM dim_safe WHERE SourceSystemKey = @src AND SafeUrlId = 'TestSafe_Entitlements')
    INSERT INTO dim_safe (SourceSystemKey, SafeUrlId, SafeName) VALUES (@src, 'TestSafe_Entitlements', 'TestSafe_Entitlements');

IF NOT EXISTS (SELECT 1 FROM dim_user WHERE SourceSystemKey = @src AND SourceUserId = 'E2E-U1')
    INSERT INTO dim_user (SourceSystemKey, SourceUserId, Username, Email) VALUES (@src, 'E2E-U1', 'e2e.resolve.target', 'e2e.resolve.target@example.test');

DECLARE @safeKey INT = (SELECT SafeKey FROM dim_safe WHERE SourceSystemKey = @src AND SafeUrlId = 'TestSafe_Entitlements');

IF NOT EXISTS (SELECT 1 FROM fact_safe_entitlement WHERE SafeKey = @safeKey AND UnresolvedMemberId = 'e2e-0000-unresolved-guid')
    INSERT INTO fact_safe_entitlement (SafeKey, MemberType, UnresolvedMemberId, MemberName, SnapshotDate)
    VALUES (@safeKey, 'User', 'e2e-0000-unresolved-guid', 'E2E Unresolved Member', CAST(SYSUTCDATETIME() AS DATE));
GO
