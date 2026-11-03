/* ============================================================================
   56_BlueTrack_ArchivedStatusAndFolder.sql

   RUN THIS AFTER 01-55. Guarded -- safe to re-run.

   - D-298: the Archived status. An archived account (for example a
     workstation account LCD moved to an archive folder and disabled with
     the CPM reason ADArchive) is out of scope for the KPIs like Risk
     Accepted / Excluded, but needs no Risk Exception, and is counted on
     its own. A Progress Rule can now archive accounts instead of moving
     them to a stage (RuleAction 'Archive', any stage, no From or To
     stage). Archived stays until a person changes the status; a rule
     doesn't archive again an account a person took out of Archived until
     the archive rules stop matching it (ArchiveRuleExempt). Archived
     accounts aren't moved between stages by rules.
   - D-298: two new Progress Rule fields: CpmDisabledReason (the reason
     automatic management is off, fact_account.ManualReason) and Folder.
   - D-299: an account's folder, from the classic account call D-280
     already makes (Self-Hosted Direct). Every cached detail is read once
     more at the next pull so the folder gets filled. The Direct pull can
     also re-read the details of accounts read more than N days ago, at
     most M per pull (off by default), as moving an account to another
     folder may not change its categoryModificationTime.
   ============================================================================ */

USE $DatabaseName$;
GO

/* ---- 1. D-298: the Archived status ------------------------------------------ */

IF NOT EXISTS (SELECT 1 FROM dbo.dim_progress_status WHERE StatusName = N'Archived')
    INSERT INTO dbo.dim_progress_status (StatusName) VALUES (N'Archived');
GO

/* The archive rule that last archived the account, and when (shown on the account page while it is Archived); and
   whether a person took it out of Archived, so the rules leave it alone until they stop matching it. */
IF COL_LENGTH('dbo.fact_account_progress', 'ArchivedByRuleKey') IS NULL
    ALTER TABLE dbo.fact_account_progress ADD ArchivedByRuleKey INT NULL, ArchivedAt DATETIME2 NULL,
        ArchiveRuleExempt BIT NOT NULL CONSTRAINT DF_fact_account_progress_ArchiveRuleExempt DEFAULT 0;
GO

/* ---- 2. D-298: archive rules -------------------------------------------------- */

IF COL_LENGTH('web.progress_rule', 'RuleAction') IS NULL
    ALTER TABLE web.progress_rule ADD RuleAction NVARCHAR(20) NOT NULL CONSTRAINT DF_progress_rule_RuleAction DEFAULT N'MoveStage'
        CONSTRAINT CK_progress_rule_RuleAction CHECK (RuleAction IN (N'MoveStage', N'Archive'));
GO
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('web.progress_rule') AND name = 'FromStageKey' AND is_nullable = 0)
    ALTER TABLE web.progress_rule ALTER COLUMN FromStageKey INT NULL;
GO
IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('web.progress_rule') AND name = 'ToStageKey' AND is_nullable = 0)
    ALTER TABLE web.progress_rule ALTER COLUMN ToStageKey INT NULL;
GO
IF OBJECT_ID('web.CK_progress_rule_Stages') IS NULL
    ALTER TABLE web.progress_rule ADD CONSTRAINT CK_progress_rule_Stages CHECK (
        (RuleAction = N'MoveStage' AND FromStageKey IS NOT NULL AND ToStageKey IS NOT NULL) OR
        (RuleAction = N'Archive' AND FromStageKey IS NULL AND ToStageKey IS NULL));
GO

/* ---- 3. D-299: the account's folder ------------------------------------------- */

IF COL_LENGTH('dbo.cyberark_direct_account_detail', 'Folder') IS NULL
BEGIN
    ALTER TABLE dbo.cyberark_direct_account_detail ADD Folder NVARCHAR(300) NULL;
    /* Read every cached account once more at the next pull, so the folder gets filled. */
    EXEC (N'UPDATE dbo.cyberark_direct_account_detail SET CategoryModificationTime = NULL;');
END
GO
IF COL_LENGTH('dbo.stg_shd_account_details', 'Folder') IS NULL
    ALTER TABLE dbo.stg_shd_account_details ADD Folder NVARCHAR(300) NULL;
GO
IF COL_LENGTH('dbo.fact_account', 'Folder') IS NULL
    ALTER TABLE dbo.fact_account ADD Folder NVARCHAR(300) NULL;                   -- e.g. Root, Archive (Self-Hosted Direct)
GO

/* Re-read the classic details of accounts read more than DetailRefreshDays ago, at most DetailRefreshMaxAccounts a pull. */
IF COL_LENGTH('web.cyberark_source_config', 'DetailRefreshEnabled') IS NULL
    ALTER TABLE web.cyberark_source_config ADD
        DetailRefreshEnabled     BIT NOT NULL CONSTRAINT DF_cyberark_source_config_DetailRefreshEnabled DEFAULT 0,
        DetailRefreshDays        INT NOT NULL CONSTRAINT DF_cyberark_source_config_DetailRefreshDays DEFAULT 7,
        DetailRefreshMaxAccounts INT NOT NULL CONSTRAINT DF_cyberark_source_config_DetailRefreshMaxAccounts DEFAULT 1000;
GO

/* As in 48_BlueTrack_SelfHostedDirectExtras.sql, plus Folder (D-299). */
CREATE OR ALTER PROCEDURE dbo.usp_Load_FactAccountClassicDetail
AS
BEGIN
    SET NOCOUNT ON;
    IF dbo.fn_SelfHostedStagedByDirect() = 0 RETURN;
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dbo.dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');

    UPDATE fa
    SET DeviceType = NULLIF(LTRIM(RTRIM(d.DeviceType)), ''),
        CreationMethod = NULLIF(LTRIM(RTRIM(d.CreationMethod)), ''),
        Folder = NULLIF(LTRIM(RTRIM(d.Folder)), ''),
        LinkedLogonAccount = CASE WHEN d.LogonName IS NOT NULL THEN
            CONCAT(d.LogonSafe, N'\', CASE WHEN ISNULL(d.LogonFolder, N'Root') = N'Root' THEN N'' ELSE d.LogonFolder + N'\' END, d.LogonName) END,
        LinkedLogonAccountKey = lg.AccountKey,
        LinkedReconcileAccount = CASE WHEN d.ReconcileName IS NOT NULL THEN
            CONCAT(d.ReconcileSafe, N'\', CASE WHEN ISNULL(d.ReconcileFolder, N'Root') = N'Root' THEN N'' ELSE d.ReconcileFolder + N'\' END, d.ReconcileName) END,
        LinkedReconcileAccountKey = rc.AccountKey
    FROM dbo.fact_account fa
    JOIN dbo.stg_shd_account_details d ON fa.SourceSystemKey = @SelfHostedKey AND fa.SourceAccountId = d.AccountId
    OUTER APPLY (SELECT TOP (1) la.AccountKey FROM dbo.fact_account la JOIN dbo.dim_safe s ON s.SafeKey = la.SafeKey
                 WHERE d.LogonName IS NOT NULL AND la.SourceSystemKey = @SelfHostedKey AND s.SafeName = d.LogonSafe AND la.AccountName = d.LogonName
                 ORDER BY la.IsDeleted, la.AccountKey) lg
    OUTER APPLY (SELECT TOP (1) ra.AccountKey FROM dbo.fact_account ra JOIN dbo.dim_safe s ON s.SafeKey = ra.SafeKey
                 WHERE d.ReconcileName IS NOT NULL AND ra.SourceSystemKey = @SelfHostedKey AND s.SafeName = d.ReconcileSafe AND ra.AccountName = d.ReconcileName
                 ORDER BY ra.IsDeleted, ra.AccountKey) rc;
END
GO

/* ---- 4. D-298: the rule fields and the Load's rule step ------------------------ */

/* As in 50_BlueTrack_CpmHealth.sql, plus CpmDisabledReason and Folder (D-298). */
CREATE OR ALTER FUNCTION dbo.fn_ProgressAccountFields(@AccountKey BIGINT)
RETURNS TABLE
AS
RETURN
SELECT v.FieldName, v.TextValue, v.DateValue
FROM dbo.fact_account fa
JOIN dbo.fact_account_progress fap ON fap.AccountKey = fa.AccountKey
JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = fa.SourceSystemKey
LEFT JOIN dbo.dim_safe sf ON sf.SafeKey = fa.SafeKey
LEFT JOIN dbo.dim_platform pl ON pl.PlatformKey = fa.PlatformKey
LEFT JOIN dbo.dim_account_type at ON at.AccountTypeKey = fap.AccountTypeKey
LEFT JOIN dbo.dim_source_of_record sor ON sor.SORKey = fap.SORKey
LEFT JOIN dbo.dim_risk_level rl ON rl.RiskLevelKey = fap.RiskLevelKey
LEFT JOIN dbo.dim_progress_status st ON st.StatusKey = fap.CurrentStatusKey
LEFT JOIN web.dim_application app ON app.ApplicationKey = fap.ApplicationKey
CROSS APPLY (VALUES
    (N'Source',              CAST(ss.SourceSystemCode AS NVARCHAR(4000)), CAST(NULL AS DATETIME2)),
    (N'SafeName',            sf.SafeName, NULL),
    (N'Folder',              fa.Folder, NULL),
    (N'PlatformId',          pl.PlatformID, NULL),
    (N'PlatformName',        pl.PlatformName, NULL),
    (N'AccountName',         fa.AccountName, NULL),
    (N'UserName',            fa.UserName, NULL),
    (N'Address',             fa.Address, NULL),
    (N'AutoManaged',         CASE fa.AutoManaged WHEN 1 THEN N'Yes' WHEN 0 THEN N'No' END, NULL),
    (N'CpmDisabledReason',   fa.ManualReason, NULL),
    (N'RequiresPsm',         CASE pl.RequirePsm WHEN 1 THEN N'Yes' WHEN 0 THEN N'No' END, NULL),
    (N'CPMStatus',           fa.CPMStatus, NULL),
    (N'SecretType',          fa.SecretType, NULL),
    (N'LastVerified',        NULL, CAST(fa.LastVerifiedDate AS DATETIME2)),
    (N'LastReconciled',      NULL, CAST(fa.LastReconciledDate AS DATETIME2)),
    (N'LastChanged',         NULL, CAST(fa.LastCPMModifiedDate AS DATETIME2)),
    (N'LastCPMAction',       fa.LastCPMAction, NULL),
    (N'LastCPMActionResult', fa.LastCPMActionResult, NULL),
    (N'LastCPMActionTime',   NULL, fa.LastCPMActionTime),
    (N'DeviceType',          fa.DeviceType, NULL),
    (N'CreationMethod',      fa.CreationMethod, NULL),
    (N'AccountType',         at.AccountTypeName, NULL),
    (N'SourceOfRecord',      sor.SORName, NULL),
    (N'RiskLevel',           rl.RiskLevelName, NULL),
    (N'Status',              st.StatusName, NULL),
    (N'OwnerName',           fap.OwnerName, NULL),
    (N'BusinessUnit',        fap.BusinessUnit, NULL),
    (N'Application',         app.ApplicationName, NULL)
) v(FieldName, TextValue, DateValue)
WHERE fa.AccountKey = @AccountKey;
GO

/* D-298: the first enabled archive rule whose conditions all match the account (a rule with no conditions never
   matches); no row when none does. */
CREATE OR ALTER FUNCTION dbo.fn_ArchiveRuleMatch(@AccountKey BIGINT)
RETURNS TABLE
AS
RETURN
SELECT TOP (1) r.ProgressRuleKey
FROM web.progress_rule r
WHERE r.IsEnabled = 1 AND r.RuleAction = N'Archive'
  AND EXISTS (SELECT 1 FROM web.progress_rule_condition c0 WHERE c0.ProgressRuleKey = r.ProgressRuleKey)
  AND NOT EXISTS (
      SELECT 1 FROM web.progress_rule_condition c
      OUTER APPLY (SELECT f.TextValue, f.DateValue FROM dbo.fn_ProgressAccountFields(@AccountKey) f WHERE f.FieldName = c.FieldName) fv
      WHERE c.ProgressRuleKey = r.ProgressRuleKey
        AND dbo.fn_ProgressConditionMatches(fv.TextValue, fv.DateValue, c.Operator, c.Value) = 0)
ORDER BY r.ProgressRuleKey;
GO

/* D-283, D-298: the Load's rule step. First the archive rules: an account (not deleted, not Risk Accepted / Excluded,
   not Archived) that an enabled archive rule matches becomes Archived, recording the rule; one a person took out of
   Archived (ArchiveRuleExempt) is left alone, and becomes eligible again once no archive rule matches it. Then the
   stage rules, as in 49, for accounts that are neither Risk Accepted / Excluded nor Archived. */
CREATE OR ALTER PROCEDURE usp_Load_AccountProgressAutoAdvance
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @ExcludedStatusKey INT = (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = N'Risk Accepted / Excluded');
    DECLARE @ArchivedStatusKey INT = (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = N'Archived');
    DECLARE @Rounds INT = (SELECT COUNT(*) FROM dbo.dim_blueprint_stage), @Moved INT = 1;

    IF @ArchivedStatusKey IS NOT NULL AND EXISTS (SELECT 1 FROM web.progress_rule WHERE IsEnabled = 1 AND RuleAction = N'Archive')
    BEGIN
        UPDATE fap SET ArchiveRuleExempt = 0
        FROM dbo.fact_account_progress fap
        WHERE fap.ArchiveRuleExempt = 1 AND NOT EXISTS (SELECT 1 FROM dbo.fn_ArchiveRuleMatch(fap.AccountKey));

        UPDATE fap
        SET CurrentStatusKey = @ArchivedStatusKey, LastUpdated = SYSUTCDATETIME(),
            ArchivedByRuleKey = m.ProgressRuleKey, ArchivedAt = SYSUTCDATETIME()
        FROM dbo.fact_account_progress fap
        JOIN dbo.fact_account fa ON fa.AccountKey = fap.AccountKey AND fa.IsDeleted = 0
        CROSS APPLY dbo.fn_ArchiveRuleMatch(fap.AccountKey) m
        WHERE fap.ArchiveRuleExempt = 0
          AND ISNULL(fap.CurrentStatusKey, -1) NOT IN (ISNULL(@ExcludedStatusKey, -1), @ArchivedStatusKey);
    END
    ELSE IF @ArchivedStatusKey IS NOT NULL
        UPDATE dbo.fact_account_progress SET ArchiveRuleExempt = 0 WHERE ArchiveRuleExempt = 1;

    WHILE @Moved > 0 AND @Rounds > 0
    BEGIN
        UPDATE fap
        SET CurrentStageKey = m.ToStageKey, LastUpdated = SYSUTCDATETIME(),
            LastAdvancedByRuleKey = m.ProgressRuleKey, LastAdvancedAt = SYSUTCDATETIME()
        FROM dbo.fact_account_progress fap
        JOIN dbo.fact_account fa ON fa.AccountKey = fap.AccountKey AND fa.IsDeleted = 0
        JOIN dbo.dim_blueprint_stage cur ON cur.StageKey = fap.CurrentStageKey
        CROSS APPLY (SELECT TOP (1) r.ProgressRuleKey, r.ToStageKey FROM web.progress_rule r
                     JOIN dbo.dim_blueprint_stage ts ON ts.StageKey = r.ToStageKey AND ts.StageOrder > cur.StageOrder
                     WHERE r.IsEnabled = 1 AND r.RuleAction = N'MoveStage' AND r.FromStageKey = fap.CurrentStageKey
                       AND EXISTS (SELECT 1 FROM web.progress_rule_condition c0 WHERE c0.ProgressRuleKey = r.ProgressRuleKey)
                       AND NOT EXISTS (
                           SELECT 1 FROM web.progress_rule_condition c
                           OUTER APPLY (SELECT f.TextValue, f.DateValue FROM dbo.fn_ProgressAccountFields(fap.AccountKey) f WHERE f.FieldName = c.FieldName) fv
                           WHERE c.ProgressRuleKey = r.ProgressRuleKey
                             AND dbo.fn_ProgressConditionMatches(fv.TextValue, fv.DateValue, c.Operator, c.Value) = 0)
                     ORDER BY ts.StageOrder DESC, r.ProgressRuleKey) m
        WHERE ISNULL(fap.CurrentStatusKey, -1) NOT IN (ISNULL(@ExcludedStatusKey, -1), ISNULL(@ArchivedStatusKey, -1));

        SET @Moved = @@ROWCOUNT;
        SET @Rounds -= 1;
    END
END
GO

/* D-283, D-298: Test on the rule page. A stage rule (@FromStageKey set): the accounts at that stage the conditions
   would move now; an archive rule (@FromStageKey NULL): the accounts at any stage they would archive now. Neither
   counts Risk Accepted / Excluded or Archived accounts; an archive rule skips accounts a person took out of Archived.
   The count, then up to 50 of them. Writes nothing. */
CREATE OR ALTER PROCEDURE dbo.usp_Preview_ProgressRule
    @FromStageKey INT,
    @Conditions   NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @ExcludedStatusKey INT = (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = N'Risk Accepted / Excluded');
    DECLARE @ArchivedStatusKey INT = (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = N'Archived');

    SELECT fap.AccountKey, fa.AccountName, fa.UserName, fa.Address, sf.SafeName
    INTO #matches
    FROM dbo.fact_account_progress fap
    JOIN dbo.fact_account fa ON fa.AccountKey = fap.AccountKey AND fa.IsDeleted = 0
    LEFT JOIN dbo.dim_safe sf ON sf.SafeKey = fa.SafeKey
    WHERE (@FromStageKey IS NULL OR fap.CurrentStageKey = @FromStageKey)
      AND (@FromStageKey IS NOT NULL OR fap.ArchiveRuleExempt = 0)
      AND ISNULL(fap.CurrentStatusKey, -1) NOT IN (ISNULL(@ExcludedStatusKey, -1), ISNULL(@ArchivedStatusKey, -1))
      AND EXISTS (SELECT 1 FROM OPENJSON(@Conditions))
      AND NOT EXISTS (
          SELECT 1 FROM OPENJSON(@Conditions) WITH (FieldName NVARCHAR(50), Operator NVARCHAR(20), Value NVARCHAR(400)) c
          OUTER APPLY (SELECT f.TextValue, f.DateValue FROM dbo.fn_ProgressAccountFields(fap.AccountKey) f WHERE f.FieldName = c.FieldName) fv
          WHERE dbo.fn_ProgressConditionMatches(fv.TextValue, fv.DateValue, c.Operator, c.Value) = 0);

    SELECT COUNT(*) FROM #matches;
    SELECT TOP (50) AccountKey, AccountName, UserName, Address, SafeName FROM #matches ORDER BY AccountName, AccountKey;
END
GO
