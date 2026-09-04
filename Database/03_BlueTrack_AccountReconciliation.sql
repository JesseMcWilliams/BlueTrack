/* ============================================================================
   03_BlueTrack_AccountReconciliation.sql

   RUN THIS FILE THIRD, after 01_BlueTrack_CreateDatabase_Schema.sql
   and 02_BlueTrack_ETL_LoadProcedures.sql.
   Includes a USE $DatabaseName$; statement below, so you don't need to
   set the database context manually before running this.

   Cross-Source Account Reconciliation -- Load Procedure & Reporting Views
   Target: SQL Server / Azure SQL

   The account_reconciliation TABLE itself is created in
   01_BlueTrack_CreateDatabase_Schema.sql (all tables live there).
   This file only adds the logic that populates and reports on it: the
   matching procedure and two views.

   PURPOSE: link a Self-Hosted fact_account row to the Privilege Cloud
   fact_account row representing the same real-world account, for
   environments that migrated from Self-Hosted to Privilege Cloud, without
   collapsing them into a single fact_account row.

   *** CRITICAL ASSUMPTION TO CONFIRM BEFORE TRUSTING THE EXACT-ID MATCH ***
   The SafeID_FileID match below only holds if Privilege Cloud is running
   against the SAME underlying vault database that Self-Hosted used (an
   in-place "lift and shift" migration that preserves internal Safe/File
   IDs). If your Privilege Cloud tenant was instead freshly provisioned and
   accounts were re-onboarded into it (a common pattern), the ID numbering
   is independently assigned by each vault and a shared SafeID/FileID
   between the two sources is COINCIDENCE, not evidence of the same
   account. I have not been told which scenario applies here -- confirm
   with whoever ran the migration before relying on Method 1 below. If
   accounts were re-onboarded rather than lifted in place, rely on Method 2
   (attribute matching) instead and treat Method 1 matches as unconfirmed.

   Because of that uncertainty, every match this logic proposes is written
   with IsConfirmed = 0 and must be reviewed by a person before being relied
   on for compliance reporting -- this script never auto-confirms a match.

   *** BATCHING NOTE ***
   CREATE OR ALTER PROCEDURE and CREATE OR ALTER VIEW must each be the only
   statement in their batch -- each is followed by GO below.
   ============================================================================ */

USE $DatabaseName$;
GO

/* ============================================================================
   Matching procedure. Proposes candidate matches only -- inserts new rows,
   never updates or deletes an existing reviewer's decision (IsConfirmed,
   ReviewedBy, RejectedFlag are left untouched on rows that already exist).
   Called as part of usp_RunFullLoad (defined in file 04), after
   usp_Load_FactAccount.
   ============================================================================ */
CREATE OR ALTER PROCEDURE usp_Load_AccountReconciliation
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');

    /* ---------------------------------------------------------------------
       METHOD 1 -- exact SourceAccountId (SafeID_FileID) match.
       See the critical assumption at the top of this file. Confidence is
       marked 'High' on the assumption that this environment did a
       lift-and-shift migration -- downgrade to 'Needs Review' in the
       UPDATE below if you confirm accounts were re-onboarded instead of
       migrated in place.
    --------------------------------------------------------------------- */
    INSERT INTO account_reconciliation (LegacyAccountKey, CurrentAccountKey, MatchMethod, MatchConfidence, Notes)
    SELECT sh.AccountKey, pc.AccountKey, 'SafeFileID_ExactMatch', 'High',
           'Matched on identical SourceAccountId (SafeID_FileID) across sources -- confirm this environment used an in-place vault migration before relying on this match.'
    FROM fact_account sh
    JOIN fact_account pc
        ON pc.SourceSystemKey = @PrivCloudKey
       AND pc.SourceAccountId = sh.SourceAccountId
       AND pc.IsDeleted = 0
    WHERE sh.SourceSystemKey = @SelfHostedKey
      AND sh.IsDeleted = 0
      AND NOT EXISTS (
          SELECT 1 FROM account_reconciliation ar
          WHERE ar.LegacyAccountKey = sh.AccountKey AND ar.CurrentAccountKey = pc.AccountKey
      );

    /* ---------------------------------------------------------------------
       METHOD 2 -- attribute-based match for Self-Hosted accounts that had
       no exact ID match above. Matches on Safe name + username + address,
       which is a reasonable fallback signal but not proof of identity --
       e.g. a decommissioned account and its replacement could share these
       same attribute values. Always leaves MatchConfidence at a level that
       forces human review; never auto-confirmed.

       If more than one Privilege Cloud candidate matches the same
       Self-Hosted account (or vice versa), ALL candidates are inserted
       with confidence 'Needs Review' rather than picking one arbitrarily --
       an ambiguous match is a worse error to hide than to surface.
    --------------------------------------------------------------------- */
    ;WITH Candidates AS (
        SELECT
            sh.AccountKey AS LegacyAccountKey,
            pc.AccountKey AS CurrentAccountKey,
            COUNT(*) OVER (PARTITION BY sh.AccountKey) AS MatchesForLegacy,
            COUNT(*) OVER (PARTITION BY pc.AccountKey) AS MatchesForCurrent
        FROM fact_account sh
        JOIN dim_safe shsafe ON shsafe.SafeKey = sh.SafeKey
        JOIN fact_account pc
            ON pc.SourceSystemKey = @PrivCloudKey
           AND pc.IsDeleted = 0
           AND pc.UserName = sh.UserName
           AND pc.Address  = sh.Address
        JOIN dim_safe pcsafe ON pcsafe.SafeKey = pc.SafeKey AND pcsafe.SafeName = shsafe.SafeName
        WHERE sh.SourceSystemKey = @SelfHostedKey
          AND sh.IsDeleted = 0
          AND sh.UserName IS NOT NULL AND sh.Address IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM account_reconciliation ar WHERE ar.LegacyAccountKey = sh.AccountKey)  -- skip accounts already matched by Method 1
    )
    INSERT INTO account_reconciliation (LegacyAccountKey, CurrentAccountKey, MatchMethod, MatchConfidence, Notes)
    SELECT LegacyAccountKey, CurrentAccountKey, 'Attribute_Match',
           CASE WHEN MatchesForLegacy = 1 AND MatchesForCurrent = 1 THEN 'Medium' ELSE 'Needs Review' END,
           CASE WHEN MatchesForLegacy > 1 OR MatchesForCurrent > 1
                THEN 'Ambiguous: matched on Safe name + username + address, but multiple candidates exist on one or both sides -- resolve manually.'
                ELSE 'Matched on Safe name + username + address (no exact ID match found). Confirm manually before treating as the same account.'
           END
    FROM Candidates c
    WHERE NOT EXISTS (
        SELECT 1 FROM account_reconciliation ar
        WHERE ar.LegacyAccountKey = c.LegacyAccountKey AND ar.CurrentAccountKey = c.CurrentAccountKey
    );
END
GO


/* ============================================================================
   Reporting view: migration status per real-world account, for Power BI.
   Three buckets: Self-Hosted only (not yet migrated / candidate for
   decommission), Migrated (confirmed match exists in both), and Privilege
   Cloud only (net-new cloud account, never existed in Self-Hosted).

   Deliberately only counts a match as "Migrated" once a human has set
   IsConfirmed = 1 and RejectedFlag = 0 -- an unreviewed proposed match does
   not count as confirmed migration for reporting purposes.
   ============================================================================ */
CREATE OR ALTER VIEW vw_account_migration_status AS
SELECT
    fa.AccountKey,
    fa.SourceAccountId,
    fa.AccountName,
    fa.UserName,
    fa.Address,
    ds.SafeName,
    CASE
        WHEN fa.SourceSystemKey = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED')
             AND EXISTS (SELECT 1 FROM account_reconciliation ar WHERE ar.LegacyAccountKey = fa.AccountKey AND ar.IsConfirmed = 1 AND ar.RejectedFlag = 0)
            THEN 'Migrated to Privilege Cloud'
        WHEN fa.SourceSystemKey = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED')
            THEN 'Self-Hosted Only (not yet migrated / unconfirmed)'
        WHEN fa.SourceSystemKey = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD')
             AND EXISTS (SELECT 1 FROM account_reconciliation ar WHERE ar.CurrentAccountKey = fa.AccountKey AND ar.IsConfirmed = 1 AND ar.RejectedFlag = 0)
            THEN 'Migrated (Cloud side of a confirmed match)'
        WHEN fa.SourceSystemKey = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD')
            THEN 'Privilege Cloud Only (net-new)'
        ELSE 'Other Source'
    END AS MigrationStatus,
    fa.IsDeleted
FROM fact_account fa
JOIN dim_safe ds ON ds.SafeKey = fa.SafeKey;
GO


/* ============================================================================
   Reviewer helper: a small queue view of everything still awaiting a human
   decision. Exposes a ReviewPriority column (ambiguous/low-confidence
   first) rather than sorting inside the view itself -- SQL Server doesn't
   allow ORDER BY in a view definition (it's rejected outright, and the
   common TOP 100 PERCENT workaround doesn't reliably preserve order on a
   later SELECT from the view either, so it isn't a real fix). Order by
   ReviewPriority, MatchedDate when you query this view:
       SELECT * FROM vw_reconciliation_review_queue
       ORDER BY ReviewPriority, MatchedDate;
   Point a simple review screen (or just SSMS) at this.
   ============================================================================ */
CREATE OR ALTER VIEW vw_reconciliation_review_queue AS
SELECT
    ar.ReconciliationKey,
    legacy.SourceAccountId AS SelfHostedAccountId, legacy.AccountName AS SelfHostedAccountName,
    legacy.UserName AS SelfHostedUserName, legacy.Address AS SelfHostedAddress,
    current_.SourceAccountId AS PrivCloudAccountId, current_.AccountName AS PrivCloudAccountName,
    current_.UserName AS PrivCloudUserName, current_.Address AS PrivCloudAddress,
    ar.MatchMethod, ar.MatchConfidence, ar.MatchedDate, ar.Notes,
    CASE ar.MatchConfidence WHEN 'Needs Review' THEN 1 WHEN 'Medium' THEN 2 WHEN 'High' THEN 3 END AS ReviewPriority
FROM account_reconciliation ar
JOIN fact_account legacy   ON legacy.AccountKey = ar.LegacyAccountKey
JOIN fact_account current_ ON current_.AccountKey = ar.CurrentAccountKey
WHERE ar.IsConfirmed = 0 AND ar.RejectedFlag = 0;
GO

PRINT 'Account reconciliation objects created successfully.';

/* ============================================================================
   Example reviewer actions (run manually, or wrap in a small app/report
   action button -- not automated, by design):

   -- Confirm a match:
   UPDATE account_reconciliation
   SET IsConfirmed = 1, ReviewedBy = 'jsmith', ReviewedDate = SYSUTCDATETIME()
   WHERE ReconciliationKey = @Key;

   -- Reject a proposed match:
   UPDATE account_reconciliation
   SET RejectedFlag = 1, ReviewedBy = 'jsmith', ReviewedDate = SYSUTCDATETIME()
   WHERE ReconciliationKey = @Key;
   ============================================================================ */
