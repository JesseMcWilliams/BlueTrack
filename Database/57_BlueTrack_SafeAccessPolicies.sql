/* ============================================================================
   57_BlueTrack_SafeAccessPolicies.sql

   RUN THIS AFTER 01-56. Guarded -- safe to re-run.

   - D-300: Safe access policies. A Safe role is a named set of Safe
     permissions (all of them held; ExactMatch: and no others). A policy
     applies to the Safes whose names match its pattern (* is any text;
     the first enabled policy in SortOrder applies) and has rules: a
     Required rule needs a member whose name matches its pattern
     ({SafeName} is the Safe's own name) holding a role, optionally with
     people in it (Vault or AD); a Forbidden rule allows no member matching
     its pattern (holding the role, when one is given). The nightly load
     checks every Safe that has accounts (usp_Check_SafeAccessPolicies)
     into dbo.safe_access_result and dbo.safe_access_problem, after the
     Safe entitlements, which now load before the Progress Rules.
   - D-301: a per-account compliance review (who and when), kept until the
     account changes Safe or someone clears it; needs the new
     ReviewAccountCompliance permission (Admin, Approver).
   - New Progress Rule fields SafeAccessCompliant and ComplianceReviewed.
   - ManageSafeAccessPolicies (Admin); audit types SafeAccessPolicyChanged
     and AccountComplianceReviewed. A disabled example policy and an
     "End user" role are seeded once, to edit.
   ============================================================================ */

USE $DatabaseName$;
GO

/* ---- 1. Safe roles and policies --------------------------------------------- */

IF OBJECT_ID('web.safe_role') IS NULL
BEGIN
    CREATE TABLE web.safe_role (
        SafeRoleKey  INT IDENTITY(1,1) CONSTRAINT PK_safe_role PRIMARY KEY,
        RoleName     NVARCHAR(100)  NOT NULL CONSTRAINT UQ_safe_role_RoleName UNIQUE,
        Description  NVARCHAR(1000) NULL,
        ExactMatch   BIT            NOT NULL CONSTRAINT DF_safe_role_ExactMatch DEFAULT 0,   -- the member holds these permissions and no others
        ModifiedBy   INT            NULL REFERENCES web.app_user(UserKey),
        ModifiedDate DATETIME2      NOT NULL CONSTRAINT DF_safe_role_ModifiedDate DEFAULT SYSUTCDATETIME()
    );
END
GO
IF OBJECT_ID('web.safe_role_permission') IS NULL
BEGIN
    CREATE TABLE web.safe_role_permission (
        SafeRoleKey   INT NOT NULL REFERENCES web.safe_role(SafeRoleKey) ON DELETE CASCADE,
        PermissionKey INT NOT NULL REFERENCES dbo.dim_permission(PermissionKey),
        CONSTRAINT PK_safe_role_permission PRIMARY KEY (SafeRoleKey, PermissionKey)
    );
END
GO

IF OBJECT_ID('web.safe_access_policy') IS NULL
BEGIN
    CREATE TABLE web.safe_access_policy (
        SafeAccessPolicyKey INT IDENTITY(1,1) CONSTRAINT PK_safe_access_policy PRIMARY KEY,
        PolicyName          NVARCHAR(200)  NOT NULL CONSTRAINT UQ_safe_access_policy_PolicyName UNIQUE,
        SafeNamePattern     NVARCHAR(300)  NOT NULL,       -- * is any text; case doesn't matter
        SortOrder           INT            NOT NULL,       -- the first enabled policy whose pattern matches applies
        IsEnabled           BIT            NOT NULL CONSTRAINT DF_safe_access_policy_IsEnabled DEFAULT 1,
        Description         NVARCHAR(1000) NULL,
        ModifiedBy          INT            NULL REFERENCES web.app_user(UserKey),
        ModifiedDate        DATETIME2      NOT NULL CONSTRAINT DF_safe_access_policy_ModifiedDate DEFAULT SYSUTCDATETIME()
    );
END
GO
IF OBJECT_ID('web.safe_access_policy_rule') IS NULL
BEGIN
    CREATE TABLE web.safe_access_policy_rule (
        SafeAccessPolicyRuleKey INT IDENTITY(1,1) CONSTRAINT PK_safe_access_policy_rule PRIMARY KEY,
        SafeAccessPolicyKey     INT           NOT NULL REFERENCES web.safe_access_policy(SafeAccessPolicyKey) ON DELETE CASCADE,
        RuleType                NVARCHAR(20)  NOT NULL CONSTRAINT CK_safe_access_policy_rule_RuleType CHECK (RuleType IN (N'Required', N'Forbidden')),
        MemberPattern           NVARCHAR(300) NOT NULL,    -- * is any text, {SafeName} the Safe's name
        SafeRoleKey             INT           NULL REFERENCES web.safe_role(SafeRoleKey),   -- Required: the role needed; Forbidden: only that role is forbidden (none: any membership)
        RequirePeople           BIT           NOT NULL CONSTRAINT DF_safe_access_policy_rule_RequirePeople DEFAULT 0,
        CONSTRAINT CK_safe_access_policy_rule_Role CHECK (RuleType = N'Forbidden' OR SafeRoleKey IS NOT NULL)
    );
    CREATE INDEX IX_safe_access_policy_rule_Policy ON web.safe_access_policy_rule (SafeAccessPolicyKey);
END
GO

/* ---- 2. Results and reviews --------------------------------------------------- */

/* The last check of each Safe that has accounts: the policy that applied (NULL: none) and whether it passed. */
IF OBJECT_ID('dbo.safe_access_result') IS NULL
BEGIN
    CREATE TABLE dbo.safe_access_result (
        SafeKey             INT       NOT NULL CONSTRAINT PK_safe_access_result PRIMARY KEY REFERENCES dbo.dim_safe(SafeKey),
        SafeAccessPolicyKey INT       NULL,     -- no FK: deleting a policy keeps the last result until the next check
        IsCompliant         BIT       NULL,     -- NULL when no policy applies
        CheckedAt           DATETIME2 NOT NULL
    );
END
GO
IF OBJECT_ID('dbo.safe_access_problem') IS NULL
BEGIN
    CREATE TABLE dbo.safe_access_problem (
        SafeAccessProblemKey    BIGINT IDENTITY(1,1) CONSTRAINT PK_safe_access_problem PRIMARY KEY,
        SafeKey                 INT            NOT NULL,
        SafeAccessPolicyRuleKey INT            NULL,
        Problem                 NVARCHAR(1000) NOT NULL
    );
    CREATE INDEX IX_safe_access_problem_Safe ON dbo.safe_access_problem (SafeKey);
END
GO

/* D-301: one review per account. SafeKey is the Safe it was in when reviewed; a move to another Safe clears it. */
IF OBJECT_ID('web.account_compliance_review') IS NULL
BEGIN
    CREATE TABLE web.account_compliance_review (
        AccountKey BIGINT         NOT NULL CONSTRAINT PK_account_compliance_review PRIMARY KEY REFERENCES dbo.fact_account(AccountKey),
        SafeKey    INT            NULL,
        ReviewedBy INT            NOT NULL REFERENCES web.app_user(UserKey),
        ReviewedAt DATETIME2      NOT NULL CONSTRAINT DF_account_compliance_review_ReviewedAt DEFAULT SYSUTCDATETIME(),
        Note       NVARCHAR(1000) NULL
    );
END
GO

/* ---- 3. The check -------------------------------------------------------------- */

/* A member or Safe name pattern as a LIKE pattern: * is any text, {SafeName} the Safe's name; the characters LIKE
   treats specially ([ % _) are matched as themselves, in the pattern and in the Safe's name. */
CREATE OR ALTER FUNCTION dbo.fn_SafeAccessLike(@Pattern NVARCHAR(300), @SafeName NVARCHAR(300))
RETURNS NVARCHAR(1500)
AS
BEGIN
    DECLARE @p NVARCHAR(1500) = REPLACE(REPLACE(REPLACE(@Pattern, N'[', N'[[]'), N'%', N'[%]'), N'_', N'[_]');
    DECLARE @s NVARCHAR(1500) = REPLACE(REPLACE(REPLACE(ISNULL(@SafeName, N''), N'[', N'[[]'), N'%', N'[%]'), N'_', N'[_]');
    RETURN REPLACE(REPLACE(@p, N'*', N'%'), N'{SafeName}', @s);
END
GO

/* D-300: checks every Safe that has an account not deleted against the first enabled policy (in SortOrder) whose Safe
   name pattern matches it, replacing dbo.safe_access_result and dbo.safe_access_problem. D-301: forgets the review of
   an account that has moved to another Safe. Called by usp_RunFullLoad (before the Progress Rules) and by Check now. */
CREATE OR ALTER PROCEDURE dbo.usp_Check_SafeAccessPolicies
AS
BEGIN
    SET NOCOUNT ON;

    DELETE r FROM web.account_compliance_review r
    JOIN dbo.fact_account fa ON fa.AccountKey = r.AccountKey
    WHERE ISNULL(fa.SafeKey, -1) <> ISNULL(r.SafeKey, -1);

    SELECT s.SafeKey, s.SafeName,
           (SELECT TOP (1) p.SafeAccessPolicyKey FROM web.safe_access_policy p
            WHERE p.IsEnabled = 1 AND s.SafeName LIKE dbo.fn_SafeAccessLike(p.SafeNamePattern, NULL)
            ORDER BY p.SortOrder, p.SafeAccessPolicyKey) AS SafeAccessPolicyKey
    INTO #safes
    FROM dbo.dim_safe s
    WHERE EXISTS (SELECT 1 FROM dbo.fact_account fa WHERE fa.SafeKey = s.SafeKey AND fa.IsDeleted = 0);

    /* Each Safe member with its name, and for each rule of the Safe's policy whose pattern it matches, whether it
       holds the rule's role (always, for a rule with none) and whether it has people (a user is one). */
    SELECT sf.SafeKey, r.SafeAccessPolicyRuleKey, e.EntitlementKey, COALESCE(u.Username, g.GroupName) AS MemberName,
           CAST(CASE WHEN r.SafeRoleKey IS NULL OR (
                    NOT EXISTS (SELECT 1 FROM web.safe_role_permission rp WHERE rp.SafeRoleKey = r.SafeRoleKey
                                AND NOT EXISTS (SELECT 1 FROM dbo.bridge_entitlement_permission bp
                                                WHERE bp.EntitlementKey = e.EntitlementKey AND bp.PermissionKey = rp.PermissionKey AND bp.IsGranted = 1))
                    AND (sr.ExactMatch = 0 OR NOT EXISTS (
                        SELECT 1 FROM dbo.bridge_entitlement_permission bp
                        WHERE bp.EntitlementKey = e.EntitlementKey AND bp.IsGranted = 1
                          AND NOT EXISTS (SELECT 1 FROM web.safe_role_permission rp WHERE rp.SafeRoleKey = r.SafeRoleKey AND rp.PermissionKey = bp.PermissionKey))))
                THEN 1 ELSE 0 END AS BIT) AS HoldsRole,
           CAST(CASE WHEN e.MemberType = 'User' OR EXISTS (SELECT 1 FROM dbo.vw_safe_access_people pp WHERE pp.EntitlementKey = e.EntitlementKey)
                THEN 1 ELSE 0 END AS BIT) AS HasPeople
    INTO #matches
    FROM #safes sf
    JOIN web.safe_access_policy_rule r ON r.SafeAccessPolicyKey = sf.SafeAccessPolicyKey
    LEFT JOIN web.safe_role sr ON sr.SafeRoleKey = r.SafeRoleKey
    JOIN dbo.fact_safe_entitlement e ON e.SafeKey = sf.SafeKey
    LEFT JOIN dbo.dim_user u ON u.UserKey = e.UserKey
    LEFT JOIN dbo.dim_group g ON g.GroupKey = e.GroupKey
    WHERE COALESCE(u.Username, g.GroupName) LIKE dbo.fn_SafeAccessLike(r.MemberPattern, sf.SafeName);

    DECLARE @problems TABLE (SafeKey INT, SafeAccessPolicyRuleKey INT, Problem NVARCHAR(1000));

    /* Required: a matching member that holds the role (and has people, when asked). Otherwise say why. */
    INSERT INTO @problems (SafeKey, SafeAccessPolicyRuleKey, Problem)
    SELECT sf.SafeKey, r.SafeAccessPolicyRuleKey, LEFT(
        CASE
            WHEN NOT EXISTS (SELECT 1 FROM #matches m WHERE m.SafeKey = sf.SafeKey AND m.SafeAccessPolicyRuleKey = r.SafeAccessPolicyRuleKey)
                THEN CONCAT(N'No member matches ', REPLACE(r.MemberPattern, N'{SafeName}', sf.SafeName), N' (needs ', sr.RoleName, N').')
            WHEN NOT EXISTS (SELECT 1 FROM #matches m WHERE m.SafeKey = sf.SafeKey AND m.SafeAccessPolicyRuleKey = r.SafeAccessPolicyRuleKey AND m.HoldsRole = 1)
                THEN CONCAT((SELECT STRING_AGG(m.MemberName, N', ') FROM #matches m WHERE m.SafeKey = sf.SafeKey AND m.SafeAccessPolicyRuleKey = r.SafeAccessPolicyRuleKey),
                            N': doesn''t hold ', sr.RoleName, CASE WHEN sr.ExactMatch = 1 THEN N' (exactly)' ELSE N'' END, N'.')
            ELSE CONCAT((SELECT STRING_AGG(m.MemberName, N', ') FROM #matches m WHERE m.SafeKey = sf.SafeKey AND m.SafeAccessPolicyRuleKey = r.SafeAccessPolicyRuleKey AND m.HoldsRole = 1),
                        N': has no people in it (Vault or AD).')
        END, 1000)
    FROM #safes sf
    JOIN web.safe_access_policy_rule r ON r.SafeAccessPolicyKey = sf.SafeAccessPolicyKey AND r.RuleType = N'Required'
    JOIN web.safe_role sr ON sr.SafeRoleKey = r.SafeRoleKey
    WHERE NOT EXISTS (SELECT 1 FROM #matches m WHERE m.SafeKey = sf.SafeKey AND m.SafeAccessPolicyRuleKey = r.SafeAccessPolicyRuleKey
                      AND m.HoldsRole = 1 AND (r.RequirePeople = 0 OR m.HasPeople = 1));

    /* Forbidden: each matching member (holding the role, when the rule names one). */
    INSERT INTO @problems (SafeKey, SafeAccessPolicyRuleKey, Problem)
    SELECT m.SafeKey, m.SafeAccessPolicyRuleKey, LEFT(
        CASE WHEN sr.RoleName IS NULL THEN CONCAT(m.MemberName, N': must not be a member.')
             ELSE CONCAT(m.MemberName, N': must not hold ', sr.RoleName, N'.') END, 1000)
    FROM #matches m
    JOIN web.safe_access_policy_rule r ON r.SafeAccessPolicyRuleKey = m.SafeAccessPolicyRuleKey AND r.RuleType = N'Forbidden'
    LEFT JOIN web.safe_role sr ON sr.SafeRoleKey = r.SafeRoleKey
    WHERE m.HoldsRole = 1;

    DELETE FROM dbo.safe_access_problem;
    INSERT INTO dbo.safe_access_problem (SafeKey, SafeAccessPolicyRuleKey, Problem)
    SELECT SafeKey, SafeAccessPolicyRuleKey, Problem FROM @problems;

    DELETE FROM dbo.safe_access_result;
    INSERT INTO dbo.safe_access_result (SafeKey, SafeAccessPolicyKey, IsCompliant, CheckedAt)
    SELECT sf.SafeKey, sf.SafeAccessPolicyKey,
           CASE WHEN sf.SafeAccessPolicyKey IS NULL THEN NULL
                WHEN EXISTS (SELECT 1 FROM @problems p WHERE p.SafeKey = sf.SafeKey) THEN 0 ELSE 1 END,
           SYSUTCDATETIME()
    FROM #safes sf;
END
GO

/* ---- 4. Progress Rule fields and the load ------------------------------------- */

/* As in 56_BlueTrack_ArchivedStatusAndFolder.sql, plus SafeAccessCompliant (D-300) and ComplianceReviewed (D-301). */
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
LEFT JOIN dbo.safe_access_result sar ON sar.SafeKey = fa.SafeKey
LEFT JOIN web.account_compliance_review acr ON acr.AccountKey = fa.AccountKey
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
    (N'SafeAccessCompliant', CASE sar.IsCompliant WHEN 1 THEN N'Yes' WHEN 0 THEN N'No' END, NULL),
    (N'ComplianceReviewed',  CASE WHEN acr.AccountKey IS NOT NULL THEN N'Yes' ELSE N'No' END, NULL),
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

/* As in 50_BlueTrack_CpmHealth.sql, with the Safe entitlements loaded and checked (D-300) before the Progress Rules,
   which can test SafeAccessCompliant. */
CREATE OR ALTER PROCEDURE usp_RunFullLoad
    @TakeSnapshot BIT = 1
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @EtlRunKey BIGINT;
    INSERT INTO dbo.etl_run_log (StepName) VALUES ('Load');
    SET @EtlRunKey = SCOPE_IDENTITY();

    BEGIN TRY
        BEGIN TRANSACTION;

        EXEC usp_Load_DimLocation;
        EXEC usp_Load_DimPlatform;
        EXEC usp_Load_DimSafe;
        EXEC usp_Load_DimUser;
        EXEC usp_Load_DimGroup;
        EXEC usp_Load_GroupMembership;
        EXEC dbo.usp_Load_AdGroupMembers;             -- D-281
        EXEC dbo.usp_Load_DimCyberArkApplication;     -- D-282
        EXEC usp_Load_FactAccount;
        EXEC dbo.usp_Load_FactAccountClassicDetail;   -- D-280
        EXEC dbo.usp_Load_CpmHealth;                  -- D-288: before the Progress Rules, which can test RequiresPsm
        EXEC usp_Load_FactSafeEntitlement;            -- D-300: moved before the Progress Rules
        EXEC dbo.usp_Check_SafeAccessPolicies;        -- D-300, D-301
        EXEC usp_Load_FactAccountProgress;
        EXEC usp_Load_AccountProgressAutoAdvance;
        EXEC usp_Load_AccountReconciliation;
        -- D-224: usp_Load_FactAccountProgressHistory moved out, to usp_RunDailySnapshot below.
        EXEC usp_DeriveTargetsFromAccountAddress;   -- D-123 (51_)
        EXEC usp_DeriveAccountTargetMap_FromPendingSafes;
        EXEC usp_DeriveAccountTargetMap_FromAddress;    -- D-193 (51_)
        EXEC usp_RecalculateRiskScores;

        COMMIT TRANSACTION;

        UPDATE dbo.etl_run_log SET Status = 'Succeeded', CompletedAt = SYSUTCDATETIME() WHERE EtlRunKey = @EtlRunKey;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        UPDATE dbo.etl_run_log
        SET Status = 'Failed', CompletedAt = SYSUTCDATETIME(), ErrorMessage = LEFT(ERROR_MESSAGE(), 2000),
            ErrorProcedure = ERROR_PROCEDURE(), ErrorLine = ERROR_LINE()
        WHERE EtlRunKey = @EtlRunKey;

        -- D-224: snapshot the unchanged state; a snapshot failure is in its own etl_run_log row.
        IF @TakeSnapshot = 1
        BEGIN
            BEGIN TRY
                EXEC dbo.usp_RunDailySnapshot;
            END TRY
            BEGIN CATCH
                IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
            END CATCH
        END;

        THROW;
    END CATCH

    IF @TakeSnapshot = 1
        EXEC dbo.usp_RunDailySnapshot;
END
GO

/* ---- 5. Permissions, audit types and the example ------------------------------ */

INSERT INTO web.app_permission (PermissionName, Description)
SELECT v.PermissionName, v.Description
FROM (VALUES
    ('ManageSafeAccessPolicies', 'Edit the Safe roles and Safe access policies, and run their check (D-300)'),
    ('ReviewAccountCompliance',  'Mark an account''s compliance as reviewed, or clear the review (D-301)')
) v(PermissionName, Description)
WHERE NOT EXISTS (SELECT 1 FROM web.app_permission p WHERE p.PermissionName = v.PermissionName);
GO
INSERT INTO web.role_permission (RoleKey, PermissionKey)
SELECT r.AppRoleKey, p.PermissionKey
FROM web.app_role r
JOIN web.app_permission p ON (r.RoleName = 'Admin' AND p.PermissionName IN ('ManageSafeAccessPolicies', 'ReviewAccountCompliance'))
                          OR (r.RoleName = 'Approver' AND p.PermissionName = 'ReviewAccountCompliance')
WHERE NOT EXISTS (SELECT 1 FROM web.role_permission rp WHERE rp.RoleKey = r.AppRoleKey AND rp.PermissionKey = p.PermissionKey);
GO

INSERT INTO web.dim_audit_event_type (EventTypeName, Description)
SELECT v.EventTypeName, v.Description
FROM (VALUES
    ('SafeAccessPolicyChanged',   'A Safe role or Safe access policy was added, changed or deleted (D-300)'),
    ('AccountComplianceReviewed', 'An account''s compliance was marked reviewed, or the review was cleared (D-301)')
) v(EventTypeName, Description)
WHERE NOT EXISTS (SELECT 1 FROM web.dim_audit_event_type t WHERE t.EventTypeName = v.EventTypeName);
GO

/* An example to edit, seeded once and disabled: the role and member names are only a suggestion. */
IF NOT EXISTS (SELECT 1 FROM web.safe_role) AND NOT EXISTS (SELECT 1 FROM web.safe_access_policy)
BEGIN
    INSERT INTO web.safe_role (RoleName, Description)
    VALUES (N'End user', N'Example: list, use and retrieve accounts. Edit to match your Safe design.');
    DECLARE @Role INT = SCOPE_IDENTITY();
    INSERT INTO web.safe_role_permission (SafeRoleKey, PermissionKey)
    SELECT @Role, PermissionKey FROM dbo.dim_permission WHERE PermissionName IN ('ListAccounts', 'UseAccounts', 'RetrieveAccounts');

    INSERT INTO web.safe_access_policy (PolicyName, SafeNamePattern, SortOrder, IsEnabled, Description)
    VALUES (N'Example: every Safe', N'*', 1, 0,
            N'Example, disabled: a {SafeName}_Users group with people in it holds End user, and Vault Admins isn''t a member. Edit the names to match yours, then enable it.');
    DECLARE @Policy INT = SCOPE_IDENTITY();
    INSERT INTO web.safe_access_policy_rule (SafeAccessPolicyKey, RuleType, MemberPattern, SafeRoleKey, RequirePeople)
    VALUES (@Policy, N'Required', N'{SafeName}_Users', @Role, 1),
           (@Policy, N'Forbidden', N'Vault Admins', NULL, 0);
END
GO
