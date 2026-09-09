/* ============================================================================
   20_BlueTrack_RiskScoringSchema.sql

   RUN THIS AFTER 01-19. Guarded (`IF OBJECT_ID(...) IS NULL` per table) --
   safe to re-run, per D-58's normal incremental-script convention.

   Design_Risk_Scoring.md (D-101-105, 2026-09-05): a computed, numeric
   account risk score -- deliberately different from dbo.dim_risk_level
   (Low/Medium/High/Critical, a manually-assigned label with no numeric
   score, which stays exactly as-is). Confirmed by direct code review
   (2026-09-09) that none of this existed yet -- this is Phase A of a
   multi-phase build (see the approved plan): the core inventory schema
   (Targets, Access Groups, the three access-mapping tables, the
   per-account computed/override score row) plus three new permissions.
   Import mapping profiles, the matching stored procedure, the scoring
   algorithm procedures, and the target-match review queue table follow in
   later phases' own scripts.
   ============================================================================ */

USE $DatabaseName$;
GO

-- web.dim_target_identifier_type: the controlled, extensible list of ways a
-- Target can be identified -- adding a new kind later (e.g. a container/pod
-- ID) is a new seeded row here, not a schema change. MatchPriority: lower
-- checked first during import matching; RequiresReview = 1 routes a
-- match-on-this-type-alone to analyst review instead of auto-merging
-- (D-102's confirmed choice for IP-only matches).
IF OBJECT_ID('web.dim_target_identifier_type', 'U') IS NULL
BEGIN
    CREATE TABLE web.dim_target_identifier_type (
        IdentifierType       NVARCHAR(50) PRIMARY KEY,
        MatchPriority           INT NOT NULL,
        RequiresReview             BIT NOT NULL DEFAULT 0
    );

    INSERT INTO web.dim_target_identifier_type (IdentifierType, MatchPriority, RequiresReview) VALUES
        ('ADGuid', 10, 0),
        ('ADSid', 10, 0),
        ('LdapBaseDN', 10, 0),
        ('DatabaseInstanceName', 10, 0),
        ('ApplianceSerialNumber', 10, 0),
        ('LinuxHostSignature', 10, 0),
        ('FQDN', 20, 0),
        ('Hostname', 30, 0),
        ('IPAddress', 40, 1);
END
GO

-- web.dim_target: any final destination an account's access leads to.
-- RiskScore is always analyst-set (0-1000), regardless of whether the row
-- itself came from AD-discovery-style ETL or an analyst's own add/bulk-CSV.
IF OBJECT_ID('web.dim_target', 'U') IS NULL
BEGIN
    CREATE TABLE web.dim_target (
        TargetKey            INT IDENTITY(1,1) PRIMARY KEY,
        TargetType             NVARCHAR(50)     NOT NULL,
        TargetName               NVARCHAR(300)    NOT NULL,
        InternalGuid               UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
        ApplicationKey               INT              NULL REFERENCES web.dim_application(ApplicationKey),
        RiskScore                     INT              NOT NULL,
        Description                     NVARCHAR(1000)   NULL,
        DiscoverySource                   NVARCHAR(100)    NULL,
        CreatedBy                          INT              NULL REFERENCES web.app_user(UserKey),
        ModifiedBy                           INT              NULL REFERENCES web.app_user(UserKey),
        ModifiedDate                           DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        ImportBatchId                             UNIQUEIDENTIFIER NULL,
        SourceFileName                              NVARCHAR(260)    NULL,
        CONSTRAINT UQ_dim_target_InternalGuid UNIQUE (InternalGuid),
        CONSTRAINT CK_dim_target_RiskScore CHECK (RiskScore BETWEEN 0 AND 1000)
    );
END
GO

-- web.target_identifier: every recognized identifier value for a target --
-- a target can (and typically will) have several. UNIQUE(IdentifierType,
-- IdentifierValue) is the database-enforced guarantee that one real
-- identifier value can only ever point at one Target.
IF OBJECT_ID('web.target_identifier', 'U') IS NULL
BEGIN
    CREATE TABLE web.target_identifier (
        TargetIdentifierKey  INT IDENTITY(1,1) PRIMARY KEY,
        TargetKey               INT NOT NULL REFERENCES web.dim_target(TargetKey),
        IdentifierType             NVARCHAR(50) NOT NULL REFERENCES web.dim_target_identifier_type(IdentifierType),
        IdentifierValue               NVARCHAR(300) NOT NULL,
        CONSTRAINT UQ_target_identifier UNIQUE (IdentifierType, IdentifierValue)
    );
END
GO

-- web.dim_access_group: a privileged-access group in the managed
-- environment (e.g. an AD "Server Admins" group) -- NOT this app's own
-- dbo.dim_group (CyberArk vault-internal Safe permissions, a different
-- concept entirely), and NOT web.identity_group_role_map (BlueTrack's own
-- login/authorization group mapping, a third, unrelated "group").
IF OBJECT_ID('web.dim_access_group', 'U') IS NULL
BEGIN
    CREATE TABLE web.dim_access_group (
        AccessGroupKey        INT IDENTITY(1,1) PRIMARY KEY,
        GroupName               NVARCHAR(300)    NOT NULL,
        GroupIdentifier           NVARCHAR(300)    NOT NULL,
        GroupScope                  NVARCHAR(20)     NOT NULL DEFAULT 'Domain',
        FoundOnTargetKey               INT              NULL REFERENCES web.dim_target(TargetKey),
        DiscoverySource                  NVARCHAR(100)    NULL,
        BaseRiskScore                      INT              NOT NULL,
        ComputedRiskScore                    INT              NULL,
        RiskScoreCalculatedDate                DATETIME2        NULL,
        IsRiskScoreStale                          BIT              NOT NULL DEFAULT 1,
        Description                                 NVARCHAR(1000)   NULL,
        CreatedBy                                     INT              NULL REFERENCES web.app_user(UserKey),
        ModifiedBy                                      INT              NULL REFERENCES web.app_user(UserKey),
        ModifiedDate                                      DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        ImportBatchId                                       UNIQUEIDENTIFIER NULL,
        SourceFileName                                        NVARCHAR(260)    NULL,
        CONSTRAINT UQ_dim_access_group UNIQUE (GroupIdentifier),
        CONSTRAINT CK_dim_access_group_BaseRiskScore CHECK (BaseRiskScore BETWEEN 0 AND 1000),
        CONSTRAINT CK_dim_access_group_ComputedRiskScore CHECK (ComputedRiskScore IS NULL OR ComputedRiskScore BETWEEN 0 AND 1000)
    );
END
GO

-- web.access_group_target_map: which targets a group grants access to.
IF OBJECT_ID('web.access_group_target_map', 'U') IS NULL
BEGIN
    CREATE TABLE web.access_group_target_map (
        AccessGroupKey        INT NOT NULL REFERENCES web.dim_access_group(AccessGroupKey),
        TargetKey                INT NOT NULL REFERENCES web.dim_target(TargetKey),
        ImportBatchId               UNIQUEIDENTIFIER NULL,
        SourceFileName                 NVARCHAR(260)    NULL,
        LoadTimestamp                     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        PRIMARY KEY (AccessGroupKey, TargetKey)
    );
END
GO

-- web.account_access_group_map: which accounts belong to which access
-- groups.
IF OBJECT_ID('web.account_access_group_map', 'U') IS NULL
BEGIN
    CREATE TABLE web.account_access_group_map (
        AccountKey            BIGINT NOT NULL REFERENCES dbo.fact_account(AccountKey),
        AccessGroupKey           INT    NOT NULL REFERENCES web.dim_access_group(AccessGroupKey),
        ImportBatchId               UNIQUEIDENTIFIER NULL,
        SourceFileName                 NVARCHAR(260)    NULL,
        LoadTimestamp                     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        PRIMARY KEY (AccountKey, AccessGroupKey)
    );
END
GO

-- web.account_target_map: DIRECT account-to-target access, bypassing any
-- group -- expected to be the exception, not the rule. SourceMethod
-- distinguishes which of the three confirmed mechanisms populated a row
-- (PendingSafeDerived: Phase C; ManualImport/ETL: Phase B).
IF OBJECT_ID('web.account_target_map', 'U') IS NULL
BEGIN
    CREATE TABLE web.account_target_map (
        AccountKey            BIGINT NOT NULL REFERENCES dbo.fact_account(AccountKey),
        TargetKey                INT    NOT NULL REFERENCES web.dim_target(TargetKey),
        SourceMethod                NVARCHAR(20)     NOT NULL,
        ImportBatchId                  UNIQUEIDENTIFIER NULL,
        SourceFileName                    NVARCHAR(260)    NULL,
        LoadTimestamp                        DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        PRIMARY KEY (AccountKey, TargetKey),
        CONSTRAINT CK_account_target_map_SourceMethod CHECK (SourceMethod IN ('PendingSafeDerived', 'ManualImport', 'ETL'))
    );
END
GO

-- web.account_risk_score: the computed, cumulative account-level score,
-- plus an analyst override that takes precedence when set. A separate
-- table rather than a column on dbo.fact_account_progress, since that
-- table lives in dbo (ETL/warehouse-refreshed) and this needs its own
-- independent staleness/recalculation lifecycle. Clearing OverrideRiskScore
-- (back to NULL) reverts to ComputedRiskScore by construction -- no extra
-- logic needed beyond the COALESCE itself.
IF OBJECT_ID('web.account_risk_score', 'U') IS NULL
BEGIN
    CREATE TABLE web.account_risk_score (
        AccountKey              BIGINT PRIMARY KEY REFERENCES dbo.fact_account(AccountKey),
        ComputedRiskScore          INT    NULL,
        RiskScoreCalculatedDate      DATETIME2 NULL,
        IsRiskScoreStale               BIT    NOT NULL DEFAULT 1,
        OverrideRiskScore                 INT    NULL,
        OverrideReason                      NVARCHAR(1000) NULL,
        OverrideSetBy                         INT    NULL REFERENCES web.app_user(UserKey),
        OverrideSetDate                          DATETIME2 NULL,
        EffectiveRiskScore                          AS (COALESCE(OverrideRiskScore, ComputedRiskScore)) PERSISTED,
        CONSTRAINT CK_account_risk_score_Computed CHECK (ComputedRiskScore IS NULL OR ComputedRiskScore BETWEEN 0 AND 1000),
        CONSTRAINT CK_account_risk_score_Override CHECK (OverrideRiskScore IS NULL OR OverrideRiskScore BETWEEN 0 AND 1000)
    );
END
GO

-- web.app_config.ActiveRiskAlgorithm: which scoring candidate is active --
-- surfaced on the existing Global Application Configuration admin page, no
-- new settings page needed for just this one switch (Phase D wires the
-- actual dispatcher/procedures this selects between).
IF COL_LENGTH('web.app_config', 'ActiveRiskAlgorithm') IS NULL
BEGIN
    ALTER TABLE web.app_config ADD ActiveRiskAlgorithm NVARCHAR(50) NOT NULL CONSTRAINT DF_app_config_ActiveRiskAlgorithm DEFAULT 'DominantPlusTail';
END
GO

-- New permissions, added after 09_BlueTrack_WebSeed.sql's own one-time
-- blanket grant already ran -- explicit catalog insert + explicit grant to
-- the bootstrap Admin role, same as every permission added since (D-98/D-115/D-118).
IF NOT EXISTS (SELECT 1 FROM web.app_permission WHERE PermissionName = 'ManageTargets')
BEGIN
    INSERT INTO web.app_permission (PermissionName, Description)
    VALUES ('ManageTargets', 'Manage the Target inventory (servers, databases, applications, etc.) for risk scoring');
END
GO

IF NOT EXISTS (SELECT 1 FROM web.app_permission WHERE PermissionName = 'ManageAccessGroups')
BEGIN
    INSERT INTO web.app_permission (PermissionName, Description)
    VALUES ('ManageAccessGroups', 'Manage Access Groups (privileged-access groups in the managed environment) for risk scoring');
END
GO

IF NOT EXISTS (SELECT 1 FROM web.app_permission WHERE PermissionName = 'ViewRiskReport')
BEGIN
    INSERT INTO web.app_permission (PermissionName, Description)
    VALUES ('ViewRiskReport', 'View the computed account risk score report');
END
GO

DECLARE @AdminRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');

DECLARE @ManageTargetsKey INT = (SELECT PermissionKey FROM web.app_permission WHERE PermissionName = 'ManageTargets');
IF @AdminRoleKey IS NOT NULL AND NOT EXISTS (SELECT 1 FROM web.role_permission WHERE RoleKey = @AdminRoleKey AND PermissionKey = @ManageTargetsKey)
BEGIN
    INSERT INTO web.role_permission (RoleKey, PermissionKey) VALUES (@AdminRoleKey, @ManageTargetsKey);
END
GO

DECLARE @AdminRoleKey2 INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');
DECLARE @ManageAccessGroupsKey INT = (SELECT PermissionKey FROM web.app_permission WHERE PermissionName = 'ManageAccessGroups');
IF @AdminRoleKey2 IS NOT NULL AND NOT EXISTS (SELECT 1 FROM web.role_permission WHERE RoleKey = @AdminRoleKey2 AND PermissionKey = @ManageAccessGroupsKey)
BEGIN
    INSERT INTO web.role_permission (RoleKey, PermissionKey) VALUES (@AdminRoleKey2, @ManageAccessGroupsKey);
END
GO

DECLARE @AdminRoleKey3 INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');
DECLARE @ViewRiskReportKey INT = (SELECT PermissionKey FROM web.app_permission WHERE PermissionName = 'ViewRiskReport');
IF @AdminRoleKey3 IS NOT NULL AND NOT EXISTS (SELECT 1 FROM web.role_permission WHERE RoleKey = @AdminRoleKey3 AND PermissionKey = @ViewRiskReportKey)
BEGIN
    INSERT INTO web.role_permission (RoleKey, PermissionKey) VALUES (@AdminRoleKey3, @ViewRiskReportKey);
END
GO

PRINT '20_BlueTrack_RiskScoringSchema.sql complete.';
