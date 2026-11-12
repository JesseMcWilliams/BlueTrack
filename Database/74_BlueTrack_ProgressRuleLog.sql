/* ============================================================================
   74_BlueTrack_ProgressRuleLog.sql

   RUN THIS AFTER 01-73. Guarded -- safe to re-run.

   D-359 (the user, 2026-11-12: "Need to add a last applied and what it
   applied to for Progress Rules."; the user chose a log of each run and
   each account a rule changed, kept for a number of days set in Settings):
   - web.app_config.ProgressRuleLogDays (1 to 3650, default 90): how long
     the log below is kept. Set on Admin > Global Application Configuration.
   - web.progress_rule_run: one row each time the Load's rule step runs
     (usp_Load_AccountProgressAutoAdvance): when it started and finished,
     and how many changes the rules made.
   - web.progress_rule_application: one row per account a rule changed in
     a run: the rule, the account, the stage it was at, the stage it moved
     to or that it was archived, and when. No foreign keys (the procedure
     writes it with OUTPUT INTO, which doesn't allow them): a deleted rule
     or account leaves its rows until they age out.
   - usp_Load_AccountProgressAutoAdvance: as in 56, plus the run and its
     changes recorded, and rows older than ProgressRuleLogDays deleted
     first.
   ============================================================================ */
USE $DatabaseName$;
GO

IF COL_LENGTH('web.app_config', 'ProgressRuleLogDays') IS NULL
    ALTER TABLE web.app_config ADD ProgressRuleLogDays INT NOT NULL
        CONSTRAINT DF_app_config_ProgressRuleLogDays DEFAULT 90
        CONSTRAINT CK_app_config_ProgressRuleLogDays CHECK (ProgressRuleLogDays BETWEEN 1 AND 3650);
GO

IF OBJECT_ID('web.progress_rule_run', 'U') IS NULL
    CREATE TABLE web.progress_rule_run (
        ProgressRuleRunKey INT IDENTITY(1,1) CONSTRAINT PK_progress_rule_run PRIMARY KEY,
        StartedAt          DATETIME2 NOT NULL,
        FinishedAt         DATETIME2 NULL,
        ChangeCount        INT       NULL
    );
GO

IF OBJECT_ID('web.progress_rule_application', 'U') IS NULL
    CREATE TABLE web.progress_rule_application (
        ProgressRuleApplicationKey BIGINT IDENTITY(1,1) CONSTRAINT PK_progress_rule_application PRIMARY KEY,
        ProgressRuleRunKey         INT       NOT NULL,
        ProgressRuleKey            INT       NOT NULL,
        AccountKey                 BIGINT    NOT NULL,
        FromStageKey               INT       NULL,
        ToStageKey                 INT       NULL,   -- NULL when the rule archived the account
        IsArchive                  BIT       NOT NULL,
        AppliedAt                  DATETIME2 NOT NULL
    );
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_progress_rule_application_Rule' AND object_id = OBJECT_ID('web.progress_rule_application'))
    CREATE INDEX IX_progress_rule_application_Rule ON web.progress_rule_application (ProgressRuleKey, ProgressRuleRunKey DESC) INCLUDE (AppliedAt);
GO
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_progress_rule_application_AppliedAt' AND object_id = OBJECT_ID('web.progress_rule_application'))
    CREATE INDEX IX_progress_rule_application_AppliedAt ON web.progress_rule_application (AppliedAt);
GO

/* D-283, D-298: the Load's rule step. First the archive rules: an account (not deleted, not Risk Accepted / Excluded,
   not Archived) that an enabled archive rule matches becomes Archived, recording the rule; one a person took out of
   Archived (ArchiveRuleExempt) is left alone, and becomes eligible again once no archive rule matches it. Then the
   stage rules, as in 49, for accounts that are neither Risk Accepted / Excluded nor Archived. D-359: each run and each
   change a rule makes is logged (web.progress_rule_run, web.progress_rule_application), kept ProgressRuleLogDays. */
CREATE OR ALTER PROCEDURE usp_Load_AccountProgressAutoAdvance
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @ExcludedStatusKey INT = (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = N'Risk Accepted / Excluded');
    DECLARE @ArchivedStatusKey INT = (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = N'Archived');
    DECLARE @Rounds INT = (SELECT COUNT(*) FROM dbo.dim_blueprint_stage), @Moved INT = 1;

    DECLARE @Keep DATETIME2 = DATEADD(DAY, -(SELECT TOP (1) ProgressRuleLogDays FROM web.app_config), SYSUTCDATETIME());
    DELETE FROM web.progress_rule_application WHERE AppliedAt < @Keep;
    DELETE FROM web.progress_rule_run WHERE StartedAt < @Keep;
    INSERT INTO web.progress_rule_run (StartedAt) VALUES (SYSUTCDATETIME());
    DECLARE @RunKey INT = SCOPE_IDENTITY();

    IF @ArchivedStatusKey IS NOT NULL AND EXISTS (SELECT 1 FROM web.progress_rule WHERE IsEnabled = 1 AND RuleAction = N'Archive')
    BEGIN
        UPDATE fap SET ArchiveRuleExempt = 0
        FROM dbo.fact_account_progress fap
        WHERE fap.ArchiveRuleExempt = 1 AND NOT EXISTS (SELECT 1 FROM dbo.fn_ArchiveRuleMatch(fap.AccountKey));

        UPDATE fap
        SET CurrentStatusKey = @ArchivedStatusKey, LastUpdated = SYSUTCDATETIME(),
            ArchivedByRuleKey = m.ProgressRuleKey, ArchivedAt = SYSUTCDATETIME()
        OUTPUT @RunKey, inserted.ArchivedByRuleKey, inserted.AccountKey, deleted.CurrentStageKey, NULL, 1, inserted.ArchivedAt
            INTO web.progress_rule_application (ProgressRuleRunKey, ProgressRuleKey, AccountKey, FromStageKey, ToStageKey, IsArchive, AppliedAt)
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
        OUTPUT @RunKey, inserted.LastAdvancedByRuleKey, inserted.AccountKey, deleted.CurrentStageKey, inserted.CurrentStageKey, 0, inserted.LastAdvancedAt
            INTO web.progress_rule_application (ProgressRuleRunKey, ProgressRuleKey, AccountKey, FromStageKey, ToStageKey, IsArchive, AppliedAt)
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

    UPDATE web.progress_rule_run
    SET FinishedAt = SYSUTCDATETIME(),
        ChangeCount = (SELECT COUNT(*) FROM web.progress_rule_application a WHERE a.ProgressRuleRunKey = @RunKey)
    WHERE ProgressRuleRunKey = @RunKey;
END
GO
