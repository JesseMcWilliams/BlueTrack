/* ============================================================================
   14_BlueTrack_FeedAccountLinking.sql

   RUN THIS AFTER 01-13. Guarded -- safe to re-run.

   Data Sources phase 4b (Planning_Data-Sources.md, D-212): a feed account
   (an AccountInventory feed's, any source but PRIVCLOUD / SELFHOSTED /
   DISCOVERY) that CyberArk also has is linked to the CyberArk account.

   - dbo.vw_linked_feed_account: each linked feed account and the CyberArk
     account it's linked to (account_reconciliation rows whose MatchMethod
     starts with 'Feed', confirmed and not rejected). Account Progress and
     the reports hide these feed accounts, so the account counts once.
   - dbo.usp_Link_FeedAccounts: links on an exact match -- user name and
     address equal, ignoring case and surrounding spaces -- to a CyberArk
     account that isn't deleted (Privilege Cloud first, if both have it). A
     pair someone rejected is never linked again. A linked feed account at
     Discovered moves to Onboarded to Vault; each link is audit-logged as a
     FieldEdit (with the stage change, if any) by @PerformedByUserKey.
   - vw_account_migration_status and vw_reconciliation_review_queue are
     about Self-Hosted -> Privilege Cloud migration; they now ignore feed
     links, which would otherwise count as migrations or wait for review.
   ============================================================================ */

USE $DatabaseName$;
GO

CREATE OR ALTER VIEW dbo.vw_linked_feed_account AS
SELECT r.LegacyAccountKey AS AccountKey, r.CurrentAccountKey AS LinkedAccountKey, r.MatchMethod, r.ReviewedDate AS LinkedDate
FROM dbo.account_reconciliation r
WHERE r.MatchMethod LIKE 'Feed%' AND r.IsConfirmed = 1 AND r.RejectedFlag = 0;
GO

CREATE OR ALTER PROCEDURE dbo.usp_Link_FeedAccounts
    @PerformedByUserKey INT,
    @SourceSystemKey INT = NULL     -- one feed source, or every feed source when NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @DiscoveredStageKey INT = (SELECT StageKey FROM dbo.dim_blueprint_stage WHERE StageName = 'Discovered');
    DECLARE @OnboardedStageKey INT = (SELECT StageKey FROM dbo.dim_blueprint_stage WHERE StageName = 'Onboarded to Vault');
    DECLARE @ReviewedBy NVARCHAR(200) = (SELECT LEFT(DisplayName, 200) FROM web.app_user WHERE UserKey = @PerformedByUserKey);

    DECLARE @Links TABLE (FeedAccountKey BIGINT PRIMARY KEY, CyberArkAccountKey BIGINT, CyberArkAccountName NVARCHAR(500), CyberArkSource NVARCHAR(200));
    DECLARE @Moved TABLE (AccountKey BIGINT PRIMARY KEY);
    DECLARE @Events TABLE (AuditEventKey BIGINT, AccountKey BIGINT);

    ;WITH feed AS (
        SELECT fa.AccountKey, LOWER(LTRIM(RTRIM(fa.UserName))) AS U, LOWER(LTRIM(RTRIM(fa.Address))) AS A
        FROM dbo.fact_account fa
        JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = fa.SourceSystemKey
        WHERE ss.SourceSystemCode NOT IN ('PRIVCLOUD', 'SELFHOSTED', 'DISCOVERY')
          AND (@SourceSystemKey IS NULL OR fa.SourceSystemKey = @SourceSystemKey)
          AND fa.IsDeleted = 0 AND fa.UserName IS NOT NULL AND fa.Address IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM dbo.vw_linked_feed_account l WHERE l.AccountKey = fa.AccountKey)
    ),
    cyberark AS (
        SELECT fa.AccountKey, fa.AccountName, ss.SourceSystemCode, ss.SourceSystemName,
               LOWER(LTRIM(RTRIM(fa.UserName))) AS U, LOWER(LTRIM(RTRIM(fa.Address))) AS A
        FROM dbo.fact_account fa
        JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = fa.SourceSystemKey
        WHERE ss.SourceSystemCode IN ('PRIVCLOUD', 'SELFHOSTED') AND fa.IsDeleted = 0
    )
    INSERT INTO @Links (FeedAccountKey, CyberArkAccountKey, CyberArkAccountName, CyberArkSource)
    SELECT f.AccountKey, m.AccountKey, m.AccountName, m.SourceSystemName
    FROM feed f
    CROSS APPLY (
        SELECT TOP (1) c.AccountKey, c.AccountName, c.SourceSystemName
        FROM cyberark c
        WHERE c.U = f.U AND c.A = f.A
          AND NOT EXISTS (SELECT 1 FROM dbo.account_reconciliation r
                          WHERE r.LegacyAccountKey = f.AccountKey AND r.CurrentAccountKey = c.AccountKey AND r.RejectedFlag = 1)
        ORDER BY CASE c.SourceSystemCode WHEN 'PRIVCLOUD' THEN 0 ELSE 1 END, c.AccountKey
    ) m;

    BEGIN TRANSACTION;

    -- A pair already proposed (phase 4c's possible matches) is confirmed; otherwise a new link.
    UPDATE r
    SET MatchMethod = 'FeedUserNameAddress', MatchConfidence = 'High', IsConfirmed = 1,
        ReviewedBy = @ReviewedBy, ReviewedDate = SYSUTCDATETIME()
    FROM dbo.account_reconciliation r
    JOIN @Links l ON l.FeedAccountKey = r.LegacyAccountKey AND l.CyberArkAccountKey = r.CurrentAccountKey;

    INSERT INTO dbo.account_reconciliation (LegacyAccountKey, CurrentAccountKey, MatchMethod, MatchConfidence, IsConfirmed, ReviewedBy, ReviewedDate)
    SELECT l.FeedAccountKey, l.CyberArkAccountKey, 'FeedUserNameAddress', 'High', 1, @ReviewedBy, SYSUTCDATETIME()
    FROM @Links l
    WHERE NOT EXISTS (SELECT 1 FROM dbo.account_reconciliation r
                      WHERE r.LegacyAccountKey = l.FeedAccountKey AND r.CurrentAccountKey = l.CyberArkAccountKey);

    UPDATE p
    SET CurrentStageKey = @OnboardedStageKey, LastUpdated = SYSUTCDATETIME()
    OUTPUT inserted.AccountKey INTO @Moved (AccountKey)
    FROM dbo.fact_account_progress p
    JOIN @Links l ON l.FeedAccountKey = p.AccountKey
    WHERE p.CurrentStageKey = @DiscoveredStageKey;

    -- D-212: one FieldEdit per link, with the stage change as a field change (as an edit on the page records it).
    INSERT INTO web.audit_event (AuditEventTypeKey, PerformedByUserKey, EntityName, EntityKey, Detail)
    OUTPUT inserted.AuditEventKey, CAST(inserted.EntityKey AS BIGINT) INTO @Events (AuditEventKey, AccountKey)
    SELECT (SELECT AuditEventTypeKey FROM web.dim_audit_event_type WHERE EventTypeName = 'FieldEdit'),
           @PerformedByUserKey, 'fact_account_progress', CAST(l.FeedAccountKey AS NVARCHAR(100)),
           LEFT(CONCAT('Linked to CyberArk account ''', l.CyberArkAccountName, ''' (', l.CyberArkSource, ', key ', l.CyberArkAccountKey,
                       '): same user name and address', CASE WHEN m.AccountKey IS NOT NULL THEN '; moved to Onboarded to Vault' ELSE '' END), 2000)
    FROM @Links l
    LEFT JOIN @Moved m ON m.AccountKey = l.FeedAccountKey;

    INSERT INTO web.audit_field_change (AuditEventKey, FieldName, OldValue, NewValue)
    SELECT e.AuditEventKey, 'CurrentStageKey', CAST(@DiscoveredStageKey AS NVARCHAR(20)), CAST(@OnboardedStageKey AS NVARCHAR(20))
    FROM @Events e
    JOIN @Moved m ON m.AccountKey = e.AccountKey;

    COMMIT TRANSACTION;

    SELECT (SELECT COUNT(*) FROM @Links) AS LinkedCount, (SELECT COUNT(*) FROM @Moved) AS MovedCount;
END
GO

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
             AND EXISTS (SELECT 1 FROM account_reconciliation ar WHERE ar.LegacyAccountKey = fa.AccountKey AND ar.IsConfirmed = 1 AND ar.RejectedFlag = 0
                         AND ar.MatchMethod NOT LIKE 'Feed%')
            THEN 'Migrated to Privilege Cloud'
        WHEN fa.SourceSystemKey = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED')
            THEN 'Self-Hosted Only (not yet migrated / unconfirmed)'
        WHEN fa.SourceSystemKey = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD')
             AND EXISTS (SELECT 1 FROM account_reconciliation ar WHERE ar.CurrentAccountKey = fa.AccountKey AND ar.IsConfirmed = 1 AND ar.RejectedFlag = 0
                         AND ar.MatchMethod NOT LIKE 'Feed%')
            THEN 'Migrated (Cloud side of a confirmed match)'
        WHEN fa.SourceSystemKey = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD')
            THEN 'Privilege Cloud Only (net-new)'
        ELSE 'Other Source'
    END AS MigrationStatus,
    fa.IsDeleted
FROM fact_account fa
JOIN dim_safe ds ON ds.SafeKey = fa.SafeKey;
GO

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
WHERE ar.IsConfirmed = 0 AND ar.RejectedFlag = 0
  AND ar.MatchMethod NOT LIKE 'Feed%';   -- D-212: feed possible matches have their own report
GO
