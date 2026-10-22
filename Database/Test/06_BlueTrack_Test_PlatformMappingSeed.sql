/* ============================================================================
   06_BlueTrack_Test_PlatformMappingSeed.sql

   Test-only synthetic data (Design_Testing-Strategy.md) -- NOT part of the
   real environment's numbered-script sequence. Never run this against
   BlueTrack (or any real environment).

   RUN THIS AFTER 05_BlueTrack_Test_DiscoveredAccountSeed.sql.

   WHAT THIS DOES (D-293): one unmapped Self-Hosted platform, WinDomain-E2E,
   for the Playwright Platform Mapping spec: its name holds the keyword
   "windomain", so the page suggests Domain Account / Active Directory. The
   spec uses the suggestion, saves it, and removes the mapping again, so it
   ends as it started.

   Guarded -- safe to re-run.
   ============================================================================ */

USE $DatabaseName$;
GO

IF NOT EXISTS (SELECT 1 FROM dbo.dim_platform WHERE SourceSystemKey = 2 AND PlatformID = N'WinDomain-E2E')
    INSERT INTO dbo.dim_platform (SourceSystemKey, PlatformID, PlatformName, Description, IsActive, PlatformType)
    VALUES (2, N'WinDomain-E2E', N'WinDomain-E2E', N'Playwright Platform Mapping spec (D-293)', 1, N'regular');
GO

PRINT '06_BlueTrack_Test_PlatformMappingSeed.sql complete.';
GO
