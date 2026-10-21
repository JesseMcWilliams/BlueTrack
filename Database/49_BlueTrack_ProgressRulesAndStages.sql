/* ============================================================================
   49_BlueTrack_ProgressRulesAndStages.sql

   RUN THIS AFTER 01-48. Guarded -- safe to re-run.

   - D-284: the Blueprint stages can be renamed, reordered, added and
     deleted on Admin > Blueprint Stages. Nothing finds a stage by its name
     any more: dim_blueprint_stage.IsStartStage marks where new accounts
     start, and Milestone marks the stage each KPI counts from (Onboarded,
     Managed, Compliant). Existing rows are marked from the seeded names.
     usp_Load_FactAccountProgress and usp_Link_FeedAccounts are recreated to
     use them (as in 02 and 14, with only the stage lookups changed).
   - D-283: Progress Rules. An account at a rule's From stage moves to its
     To stage (a later one; the next by default on the page) when every
     condition of an enabled rule matches its properties; forward only,
     repeated so it can move several stages in one load; never one that is
     Risk Accepted / Excluded. Replaces the fixed
     rule of D-91/D-212 (usp_Load_AccountProgressAutoAdvance keeps its name
     and place in the load), which is seeded as the first rule.
   - D-285: rejected sign-ins: a 'BlueTrack sign-in (system)' user the
     LogonFailed audit events are attributed to, and the SignInRejected
     notification type (off).
   - D-287: the SamlCertificateExpiring notification type (off).
   - The ManageProgressRules permission, granted to Admin.
   ============================================================================ */

USE $DatabaseName$;
GO

/* ---- 1. D-284: stage flags ------------------------------------------------- */

IF COL_LENGTH('dbo.dim_blueprint_stage', 'IsStartStage') IS NULL
    ALTER TABLE dbo.dim_blueprint_stage ADD IsStartStage BIT NOT NULL CONSTRAINT DF_dim_blueprint_stage_IsStartStage DEFAULT 0;
GO
IF COL_LENGTH('dbo.dim_blueprint_stage', 'Milestone') IS NULL
    ALTER TABLE dbo.dim_blueprint_stage ADD Milestone NVARCHAR(20) NULL
        CONSTRAINT CK_dim_blueprint_stage_Milestone CHECK (Milestone IN (N'Onboarded', N'Managed', N'Compliant'));
GO
/* One start stage and each milestone on one stage at most: the API (BlueprintStageRepository) keeps to that in one
   transaction. Not filtered unique indexes, which would tie every write to the table to QUOTED_IDENTIFIER ON. */

/* Marked once, from the seeded names (else by position, as the KPIs counted before: 3, 4, 5). */
IF NOT EXISTS (SELECT 1 FROM dbo.dim_blueprint_stage WHERE IsStartStage = 1)
    UPDATE dbo.dim_blueprint_stage SET IsStartStage = 1
    WHERE StageKey = COALESCE((SELECT StageKey FROM dbo.dim_blueprint_stage WHERE StageName = N'Discovered'),
                              (SELECT TOP (1) StageKey FROM dbo.dim_blueprint_stage ORDER BY StageOrder, StageKey));
IF NOT EXISTS (SELECT 1 FROM dbo.dim_blueprint_stage WHERE Milestone IS NOT NULL)
BEGIN
    UPDATE s SET Milestone = m.Milestone
    FROM dbo.dim_blueprint_stage s
    JOIN (VALUES (N'Onboarded to Vault', 3, N'Onboarded'), (N'Managed / Rotation Enabled', 4, N'Managed'), (N'Verified / Compliant', 5, N'Compliant')) m(StageName, StageOrder, Milestone)
      ON s.StageKey = COALESCE((SELECT x.StageKey FROM dbo.dim_blueprint_stage x WHERE x.StageName = m.StageName),
                               (SELECT TOP (1) x.StageKey FROM dbo.dim_blueprint_stage x WHERE x.StageOrder = m.StageOrder ORDER BY x.StageKey));
END
GO

CREATE OR ALTER FUNCTION dbo.fn_StartStageKey()
RETURNS INT
AS
BEGIN
    RETURN (SELECT TOP (1) StageKey FROM dbo.dim_blueprint_stage WHERE IsStartStage = 1);
END
GO

/* The stage a KPI milestone (Onboarded, Managed, Compliant) is marked on; NULL when no stage is. */
CREATE OR ALTER FUNCTION dbo.fn_MilestoneStageKey(@Milestone NVARCHAR(20))
RETURNS INT
AS
BEGIN
    RETURN (SELECT TOP (1) StageKey FROM dbo.dim_blueprint_stage WHERE Milestone = @Milestone);
END
GO

/* Its order: an account counts for the KPI at that stage or any later one. */
CREATE OR ALTER FUNCTION dbo.fn_MilestoneStageOrder(@Milestone NVARCHAR(20))
RETURNS INT
AS
BEGIN
    RETURN (SELECT TOP (1) StageOrder FROM dbo.dim_blueprint_stage WHERE Milestone = @Milestone);
END
GO

/* As in 02_BlueTrack_Baseline_EtlLoads.sql, with the start stage from IsStartStage. */
CREATE OR ALTER PROCEDURE usp_Load_FactAccountProgress
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @DiscoveredStageKey INT = dbo.fn_StartStageKey();   -- D-284: the stage marked "new accounts start here"
    DECLARE @NotStartedStatusKey INT = (SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'Not Started');

    INSERT INTO fact_account_progress (AccountKey, CurrentStageKey, CurrentStatusKey, AccountTypeKey, SORKey)
    SELECT fa.AccountKey, @DiscoveredStageKey, @NotStartedStatusKey, pm.AccountTypeKey, pm.SORKey
    FROM fact_account fa
    LEFT JOIN platform_account_type_map pm ON pm.PlatformKey = fa.PlatformKey
    WHERE fa.IsDeleted = 0
      AND NOT EXISTS (SELECT 1 FROM fact_account_progress fap WHERE fap.AccountKey = fa.AccountKey);
END
GO

/* As in 14_BlueTrack_FeedAccountLinking.sql, with the start and Onboarded stages from the flags. */
CREATE OR ALTER PROCEDURE dbo.usp_Link_FeedAccounts
    @PerformedByUserKey INT,
    @SourceSystemKey INT = NULL     -- one feed source, or every feed source when NULL
AS
BEGIN
    SET NOCOUNT ON;
    SET XACT_ABORT ON;

    DECLARE @DiscoveredStageKey INT = dbo.fn_StartStageKey();   -- D-284
    DECLARE @OnboardedStageKey INT = dbo.fn_MilestoneStageKey(N'Onboarded');   -- D-284: the stage marked Onboarded
    DECLARE @OnboardedStageName NVARCHAR(100) = (SELECT StageName FROM dbo.dim_blueprint_stage WHERE StageKey = @OnboardedStageKey);
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
    WHERE p.CurrentStageKey = @DiscoveredStageKey AND @OnboardedStageKey IS NOT NULL;

    -- D-212: one FieldEdit per link, with the stage change as a field change (as an edit on the page records it).
    INSERT INTO web.audit_event (AuditEventTypeKey, PerformedByUserKey, EntityName, EntityKey, Detail)
    OUTPUT inserted.AuditEventKey, CAST(inserted.EntityKey AS BIGINT) INTO @Events (AuditEventKey, AccountKey)
    SELECT (SELECT AuditEventTypeKey FROM web.dim_audit_event_type WHERE EventTypeName = 'FieldEdit'),
           @PerformedByUserKey, 'fact_account_progress', CAST(l.FeedAccountKey AS NVARCHAR(100)),
           LEFT(CONCAT('Linked to CyberArk account ''', l.CyberArkAccountName, ''' (', l.CyberArkSource, ', key ', l.CyberArkAccountKey,
                       '): same user name and address', CASE WHEN m.AccountKey IS NOT NULL THEN '; moved to ' + @OnboardedStageName ELSE '' END), 2000)
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

/* ---- 2. D-283: Progress Rules ---------------------------------------------- */

IF OBJECT_ID('web.progress_rule') IS NULL
BEGIN
    CREATE TABLE web.progress_rule (
        ProgressRuleKey INT IDENTITY(1,1) CONSTRAINT PK_progress_rule PRIMARY KEY,
        RuleName        NVARCHAR(200)  NOT NULL,
        FromStageKey    INT            NOT NULL REFERENCES dbo.dim_blueprint_stage(StageKey),
        ToStageKey      INT            NOT NULL REFERENCES dbo.dim_blueprint_stage(StageKey),
        IsEnabled       BIT            NOT NULL CONSTRAINT DF_progress_rule_IsEnabled DEFAULT 1,
        Description     NVARCHAR(1000) NULL,
        ModifiedBy      INT            NULL REFERENCES web.app_user(UserKey),
        ModifiedDate    DATETIME2      NOT NULL CONSTRAINT DF_progress_rule_ModifiedDate DEFAULT SYSUTCDATETIME()
    );
END
GO

/* Every condition of a rule must match. FieldName and Operator are checked by the API (ProgressRuleFields). */
IF OBJECT_ID('web.progress_rule_condition') IS NULL
BEGIN
    CREATE TABLE web.progress_rule_condition (
        ProgressRuleConditionKey INT IDENTITY(1,1) CONSTRAINT PK_progress_rule_condition PRIMARY KEY,
        ProgressRuleKey          INT           NOT NULL REFERENCES web.progress_rule(ProgressRuleKey) ON DELETE CASCADE,
        FieldName                NVARCHAR(50)  NOT NULL,
        Operator                 NVARCHAR(20)  NOT NULL,
        Value                    NVARCHAR(400) NULL
    );
    CREATE INDEX IX_progress_rule_condition_Rule ON web.progress_rule_condition (ProgressRuleKey);
END
GO

/* Which rule last moved the account, and when (shown on the account page). No FK: deleting a rule keeps the record. */
IF COL_LENGTH('dbo.fact_account_progress', 'LastAdvancedByRuleKey') IS NULL
    ALTER TABLE dbo.fact_account_progress ADD LastAdvancedByRuleKey INT NULL, LastAdvancedAt DATETIME2 NULL;
GO

/* An account's properties a condition can test, by field name: text, or a date for the date fields. */
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
    (N'PlatformId',          pl.PlatformID, NULL),
    (N'PlatformName',        pl.PlatformName, NULL),
    (N'AccountName',         fa.AccountName, NULL),
    (N'UserName',            fa.UserName, NULL),
    (N'Address',             fa.Address, NULL),
    (N'AutoManaged',         CASE fa.AutoManaged WHEN 1 THEN N'Yes' WHEN 0 THEN N'No' END, NULL),
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

/* Whether one condition holds. Text compares without case (the database collation) after trimming; a blank value is
   empty. The "not" operators hold for an empty value. Days are counted back from now (UTC). */
CREATE OR ALTER FUNCTION dbo.fn_ProgressConditionMatches(@Text NVARCHAR(4000), @Date DATETIME2, @Operator NVARCHAR(20), @Value NVARCHAR(400))
RETURNS BIT
AS
BEGIN
    DECLARE @t NVARCHAR(4000) = NULLIF(LTRIM(RTRIM(@Text)), N'');
    DECLARE @v NVARCHAR(400) = LTRIM(RTRIM(ISNULL(@Value, N'')));
    DECLARE @days INT = TRY_CAST(@v AS INT);
    DECLARE @inList BIT = CASE WHEN @t IS NOT NULL AND EXISTS (SELECT 1 FROM STRING_SPLIT(@v, N',') s WHERE LTRIM(RTRIM(s.value)) = @t) THEN 1 ELSE 0 END;
    RETURN CASE @Operator
        WHEN N'Equals'        THEN CASE WHEN @t = @v THEN 1 ELSE 0 END
        WHEN N'NotEquals'     THEN CASE WHEN @t = @v THEN 0 ELSE 1 END
        WHEN N'Contains'      THEN CASE WHEN @t IS NOT NULL AND @v <> N'' AND CHARINDEX(@v, @t) > 0 THEN 1 ELSE 0 END
        WHEN N'NotContains'   THEN CASE WHEN @t IS NOT NULL AND @v <> N'' AND CHARINDEX(@v, @t) > 0 THEN 0 ELSE 1 END
        WHEN N'StartsWith'    THEN CASE WHEN @t IS NOT NULL AND @v <> N'' AND LEFT(@t, LEN(@v)) = @v THEN 1 ELSE 0 END
        WHEN N'In'            THEN @inList
        WHEN N'NotIn'         THEN 1 - @inList
        WHEN N'IsEmpty'       THEN CASE WHEN @t IS NULL AND @Date IS NULL THEN 1 ELSE 0 END
        WHEN N'IsNotEmpty'    THEN CASE WHEN @t IS NOT NULL OR @Date IS NOT NULL THEN 1 ELSE 0 END
        WHEN N'WithinDays'    THEN CASE WHEN @days IS NOT NULL AND @Date >= DATEADD(DAY, -@days, CAST(SYSUTCDATETIME() AS DATE)) THEN 1 ELSE 0 END
        WHEN N'OlderThanDays' THEN CASE WHEN @days IS NOT NULL AND @Date < DATEADD(DAY, -@days, CAST(SYSUTCDATETIME() AS DATE)) THEN 1 ELSE 0 END
        ELSE 0
    END;
END
GO

/* D-283: the Load's stage step. For each account (not deleted, not Risk Accepted / Excluded) at a stage with an
   enabled rule whose conditions all match (a rule with no conditions never matches), move it to the rule's To stage
   (the furthest, when several match; never back), keep its status, and record the rule. Repeated so an account can
   move on again from its new stage in the same load. Replaces the fixed rule of D-91/D-212 (seeded below). */
CREATE OR ALTER PROCEDURE usp_Load_AccountProgressAutoAdvance
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @ExcludedStatusKey INT = (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = N'Risk Accepted / Excluded');
    DECLARE @Rounds INT = (SELECT COUNT(*) FROM dbo.dim_blueprint_stage), @Moved INT = 1;

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
                     WHERE r.IsEnabled = 1 AND r.FromStageKey = fap.CurrentStageKey
                       AND EXISTS (SELECT 1 FROM web.progress_rule_condition c0 WHERE c0.ProgressRuleKey = r.ProgressRuleKey)
                       AND NOT EXISTS (
                           SELECT 1 FROM web.progress_rule_condition c
                           OUTER APPLY (SELECT f.TextValue, f.DateValue FROM dbo.fn_ProgressAccountFields(fap.AccountKey) f WHERE f.FieldName = c.FieldName) fv
                           WHERE c.ProgressRuleKey = r.ProgressRuleKey
                             AND dbo.fn_ProgressConditionMatches(fv.TextValue, fv.DateValue, c.Operator, c.Value) = 0)
                     ORDER BY ts.StageOrder DESC, r.ProgressRuleKey) m
        WHERE (@ExcludedStatusKey IS NULL OR ISNULL(fap.CurrentStatusKey, -1) <> @ExcludedStatusKey);

        SET @Moved = @@ROWCOUNT;
        SET @Rounds -= 1;
    END
END
GO

/* D-283: Test on the rule page: how many accounts at @FromStageKey the conditions (JSON array of {FieldName, Operator,
   Value}) would move now, and up to 50 of them. Writes nothing. */
CREATE OR ALTER PROCEDURE dbo.usp_Preview_ProgressRule
    @FromStageKey INT,
    @Conditions   NVARCHAR(MAX)
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @ExcludedStatusKey INT = (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = N'Risk Accepted / Excluded');

    SELECT fap.AccountKey, fa.AccountName, fa.UserName, fa.Address, sf.SafeName
    INTO #matches
    FROM dbo.fact_account_progress fap
    JOIN dbo.fact_account fa ON fa.AccountKey = fap.AccountKey AND fa.IsDeleted = 0
    LEFT JOIN dbo.dim_safe sf ON sf.SafeKey = fa.SafeKey
    WHERE fap.CurrentStageKey = @FromStageKey
      AND (@ExcludedStatusKey IS NULL OR ISNULL(fap.CurrentStatusKey, -1) <> @ExcludedStatusKey)
      AND EXISTS (SELECT 1 FROM OPENJSON(@Conditions))
      AND NOT EXISTS (
          SELECT 1 FROM OPENJSON(@Conditions) WITH (FieldName NVARCHAR(50), Operator NVARCHAR(20), Value NVARCHAR(400)) c
          OUTER APPLY (SELECT f.TextValue, f.DateValue FROM dbo.fn_ProgressAccountFields(fap.AccountKey) f WHERE f.FieldName = c.FieldName) fv
          WHERE dbo.fn_ProgressConditionMatches(fv.TextValue, fv.DateValue, c.Operator, c.Value) = 0);

    SELECT COUNT(*) FROM #matches;
    SELECT TOP (50) AccountKey, AccountName, UserName, Address, SafeName FROM #matches ORDER BY AccountName, AccountKey;
END
GO

/* The fixed rule it replaces (D-91, D-212): a CyberArk account not in a pending Safe is in the Vault, so it moves from
   the start stage straight to the Onboarded stage. Seeded once; the condition values match the old rule exactly. */
IF NOT EXISTS (SELECT 1 FROM web.progress_rule)
BEGIN
    DECLARE @Start INT = dbo.fn_StartStageKey(), @Onboarded INT = dbo.fn_MilestoneStageKey(N'Onboarded');
    IF @Start IS NOT NULL AND @Onboarded IS NOT NULL
    BEGIN
        INSERT INTO web.progress_rule (RuleName, FromStageKey, ToStageKey, IsEnabled, Description)
        VALUES (N'In the Vault, not in a pending Safe', @Start, @Onboarded, 1,
                N'A CyberArk account (Privilege Cloud or Self-Hosted) whose Safe name doesn''t contain _Pending. Was the built-in rule before Progress Rules (D-91, D-212).');
        DECLARE @Rule INT = SCOPE_IDENTITY();
        INSERT INTO web.progress_rule_condition (ProgressRuleKey, FieldName, Operator, Value) VALUES
            (@Rule, N'Source', N'In', N'PRIVCLOUD, SELFHOSTED'),
            (@Rule, N'SafeName', N'NotContains', N'_Pending');
    END
END
GO

/* ---- 3. Permission, system user, notification and audit types ------------- */

IF NOT EXISTS (SELECT 1 FROM web.app_permission WHERE PermissionName = 'ManageProgressRules')
    INSERT INTO web.app_permission (PermissionName, Description)
    VALUES ('ManageProgressRules', 'Edit the Blueprint stages and the Progress Rules that move accounts between them');
GO
INSERT INTO web.role_permission (RoleKey, PermissionKey)
SELECT r.AppRoleKey, p.PermissionKey
FROM web.app_role r
CROSS JOIN web.app_permission p
WHERE r.RoleName = 'Admin' AND p.PermissionName = 'ManageProgressRules'
  AND NOT EXISTS (SELECT 1 FROM web.role_permission rp WHERE rp.RoleKey = r.AppRoleKey AND rp.PermissionKey = p.PermissionKey);
GO

INSERT INTO web.dim_audit_event_type (EventTypeName, Description)
SELECT v.EventTypeName, v.Description
FROM (VALUES
    ('ProgressRuleChanged',   'A Progress Rule was added, changed or deleted (D-283)'),
    ('BlueprintStageChanged', 'A Blueprint stage was added, renamed, reordered, re-flagged or deleted (D-284)')
) v(EventTypeName, Description)
WHERE NOT EXISTS (SELECT 1 FROM web.dim_audit_event_type t WHERE t.EventTypeName = v.EventTypeName);
GO

/* D-285: a rejected SAML or OIDC sign-in has no user yet; its LogonFailed event is attributed to this one, which
   (like the Data Feeds user) has no identity anyone can sign in with. */
DECLARE @WinIntProviderKey INT = (SELECT TOP (1) ProviderKey FROM web.identity_provider_config WHERE ProviderType = 'WindowsIntegrated');
IF @WinIntProviderKey IS NOT NULL AND NOT EXISTS (SELECT 1 FROM web.app_user WHERE ProviderKey = @WinIntProviderKey AND ExternalIdentifier = 'S-1-0-0-signin')
    INSERT INTO web.app_user (ProviderKey, ExternalIdentifier, DisplayName)
    VALUES (@WinIntProviderKey, 'S-1-0-0-signin', 'BlueTrack sign-in (system)');
GO

INSERT INTO web.dim_notification_type (NotificationTypeName, Description, IsEnabled)
SELECT v.Name, v.Description, 0
FROM (VALUES
    ('SignInRejected', 'When it happens: a SAML or OIDC sign-in was rejected (bad signature, audience, time, replay, or the identity provider refused it). At most one email per 15 minutes; every rejection is in the Audit Log as LogonFailed (D-285).'),
    ('SamlCertificateExpiring', 'Once a day, from the daily digest time: the SAML identity provider''s certificate or BlueTrack''s own SAML certificate expires within 30 days, within 7 days, has expired, or isn''t installed (D-287).')
) v(Name, Description)
WHERE NOT EXISTS (SELECT 1 FROM web.dim_notification_type t WHERE t.NotificationTypeName = v.Name);
GO

PRINT '49_BlueTrack_ProgressRulesAndStages.sql complete.';
GO
