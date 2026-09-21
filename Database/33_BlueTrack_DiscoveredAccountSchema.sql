/* ============================================================================
   33_BlueTrack_DiscoveredAccountSchema.sql

   RUN THIS AFTER 01-32. Guarded (IF OBJECT_ID(...) IS NULL per table) --
   safe to re-run, per D-58's normal incremental-script convention.

   AD Account Discovery feature (2026-09-16), Phase C: candidate AD accounts
   -- real accounts that exist in Active Directory but aren't onboarded into
   CyberArk yet (no dbo.fact_account row) -- found by matching AD group
   membership against web.dim_access_group's already-inventoried groups
   (App/Api/AdDiscovery/AdAccountDiscoveryService.cs).

   Deliberately a new table in the web schema, not a revival of
   dbo.stg_discovered_accounts (an undocumented, content-free stub
   predating this feature entirely -- LoadId/ImportBatchId/SourceFileName/
   LoadTimestamp/RawColumnsPending only, shaped for the CSV/BULK-INSERT ETL
   pattern this feature's data doesn't arrive through). This data comes from
   a live LDAP query in C#, not a file, and conflating "AD candidate pending
   a decision" with the CyberArk-account staging convention would be
   confusing -- a purpose-built table alongside the rest of Risk Scoring's
   own web-schema tables is cleaner.

   PossibleExistingAccountKey is nullable and set only when the discovery
   job's best-effort text match (UserName/PlatformLogonDomain, normalized)
   found a plausible-but-not-certain existing dbo.fact_account row --
   surfaced for human review on the new report, never used to silently
   include or exclude a candidate.

   UNIQUE (DomainName, SamAccountName) is the upsert key: a re-run updates
   LastSeenDate/re-scores an existing row rather than duplicating it. A
   candidate that stops matching every previously-matched group on a later
   run is left in place, not deleted -- pruning stale entries is an open
   question for a later pass, not solved here.
   ============================================================================ */

USE $DatabaseName$;
GO

IF OBJECT_ID('web.discovered_account', 'U') IS NULL
BEGIN
    CREATE TABLE web.discovered_account (
        DiscoveredAccountKey     INT IDENTITY(1,1) PRIMARY KEY,
        DomainName                  NVARCHAR(100)    NOT NULL,
        SamAccountName                  NVARCHAR(300)    NOT NULL,
        DistinguishedName                   NVARCHAR(1000)   NULL,
        ObjectSid                              NVARCHAR(200)    NULL,
        DisplayName                                NVARCHAR(300)    NULL,
        IsEnabled                                      BIT              NOT NULL DEFAULT 1,
        ComputedRiskScore                                  INT              NULL,
        RiskScoreCalculatedDate                                DATETIME2        NULL,
        DiscoveredDate                                             DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        LastSeenDate                                                   DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        PossibleExistingAccountKey                                         BIGINT           NULL REFERENCES dbo.fact_account(AccountKey),
        CONSTRAINT UQ_discovered_account_Domain_SamAccountName UNIQUE (DomainName, SamAccountName)
    );
END
GO

-- Which of the already-inventoried Access Groups a candidate's real AD
-- group membership matched -- mirrors web.account_access_group_map's own
-- shape, just for a candidate row instead of a real fact_account row.
IF OBJECT_ID('web.discovered_account_access_group_map', 'U') IS NULL
BEGIN
    CREATE TABLE web.discovered_account_access_group_map (
        DiscoveredAccountKey INT NOT NULL REFERENCES web.discovered_account(DiscoveredAccountKey),
        AccessGroupKey          INT NOT NULL REFERENCES web.dim_access_group(AccessGroupKey),
        PRIMARY KEY (DiscoveredAccountKey, AccessGroupKey)
    );
END
GO

PRINT '33_BlueTrack_DiscoveredAccountSchema.sql complete.';
