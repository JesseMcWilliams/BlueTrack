/* ============================================================================
   06_BlueTrack_WebInterface_Schema.sql

   RUN THIS AFTER 01-05.

   Blueprint Progress Tracking Web Interface -- Schema Creation
   Target: SQL Server / Azure SQL

   Implements every table designed across the .md files in Design Documents
   during the 2026-08-27 web interface design session, referenced below by decision ID
   (see Design_Decision_Register.md for the full record of each decision).

   SCHEMA SEPARATION (D-64): every table in this file lives in a new `web`
   schema, not the default `dbo` schema the rest of this project uses for
   the CyberArk-mirroring/ETL tables. A separate *database* was considered
   and rejected -- several of these tables have real foreign keys into dbo
   tables (risk_exception.AccountKey -> dbo.fact_account, for example), and
   SQL Server does not support cross-database FK enforcement. A schema
   captures the organizational/permission-grant benefit without giving up
   referential integrity or needing distributed transactions.

   THIS FILE DOES NOT DROP THE DATABASE. Unlike 01, which is safe to
   destructively recreate because it always starts from an empty database,
   this file may run against an environment where 01-05 have already loaded
   real bulk/tracking data (dbo.fact_account_progress, etc.) that must not
   be touched. Making this file re-runnable in Dev therefore needs a
   different pattern than 01's per-table drop-then-create:
     1. First, drop any FK constraint that points INTO a web.* table from
        outside it -- both from dbo tables altered further down
        (dim_safe.ApplicationKey, fact_account_progress.ExceptionKey) and
        the circular one between web.identity_provider_config and
        web.app_user -- then drop every web.* table that exists, in reverse
        dependency order. Without dropping those inbound constraints first,
        a re-run fails with "referenced by a FOREIGN KEY constraint" the
        moment any prior run has already added them.
     2. Then create every web.* table in dependency order (parents before
        children), with seed data inline where the design docs confirmed
        specific values (not where they were flagged as illustrative).
     3. Additions to existing dbo tables (dim_safe.ApplicationKey,
        fact_account_progress.ExceptionKey) never drop a column or table --
        the column-add and the FK-constraint-add are guarded independently
        (COL_LENGTH for the column, sys.foreign_keys by name for the
        constraint) so a re-run can restore just the constraint that step 1
        removed without erroring on "column already exists".

   PAST THIS POINT: per D-58, once an environment holds real data, further
   schema changes are promoted via DbUp-run, hand-written numbered scripts
   (07, 08, ...) that only add/alter -- this file itself is never edited or
   re-run destructively against a live environment once applied there.
   ============================================================================ */

USE $DatabaseName$;
GO


/* ============================================================================
   0. SCHEMA
   ============================================================================ */
IF SCHEMA_ID('web') IS NULL
BEGIN
    EXEC('CREATE SCHEMA web AUTHORIZATION dbo');
END
GO


/* ============================================================================
   1. CLEANUP PASS -- drop existing web.* tables in reverse dependency order,
      so this file can be re-run from the top while iterating in Dev.
      See header note above for why this differs from 01's per-table guard.

      1a drops inbound FK constraints first -- both the ones this file adds
      to dbo tables further down (dim_safe.ApplicationKey,
      fact_account_progress.ExceptionKey) and the circular one between
      web.identity_provider_config and web.app_user -- so 1b's table drops
      never fail with "referenced by a FOREIGN KEY constraint" on a re-run.
   ============================================================================ */

-- 1a. Drop inbound FK constraints before dropping the tables they reference.
-- Looked up dynamically by table/column rather than by the fixed names
-- used later in this file: an earlier iteration of this script (before
-- these constraints were explicitly named) may have left one behind under
-- a system-generated name that would never match a hardcoded guess.
DECLARE @dropFkSql NVARCHAR(MAX) = N'';

SELECT @dropFkSql = @dropFkSql
    + N'ALTER TABLE ' + QUOTENAME(SCHEMA_NAME(t.schema_id)) + N'.' + QUOTENAME(t.name)
    + N' DROP CONSTRAINT ' + QUOTENAME(fk.name) + N';' + CHAR(10)
FROM sys.foreign_keys fk
JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
JOIN sys.tables t ON t.object_id = fk.parent_object_id
JOIN sys.columns c ON c.object_id = fkc.parent_object_id AND c.column_id = fkc.parent_column_id
WHERE (t.schema_id = SCHEMA_ID('dbo') AND t.name = 'dim_safe' AND c.name = 'ApplicationKey')
   OR (t.schema_id = SCHEMA_ID('dbo') AND t.name = 'fact_account_progress' AND c.name = 'ExceptionKey')
   OR (t.schema_id = SCHEMA_ID('web') AND t.name = 'identity_provider_config' AND c.name IN ('CreatedBy', 'ModifiedBy'));

EXEC sp_executesql @dropFkSql;
GO

-- 1b. Drop web.* tables in reverse dependency order.
IF OBJECT_ID('web.account_progress_field_metadata', 'U') IS NOT NULL DROP TABLE web.account_progress_field_metadata;
IF OBJECT_ID('web.app_config', 'U') IS NOT NULL DROP TABLE web.app_config;
IF OBJECT_ID('web.audit_config', 'U') IS NOT NULL DROP TABLE web.audit_config;
IF OBJECT_ID('web.audit_purge_log', 'U') IS NOT NULL DROP TABLE web.audit_purge_log;
IF OBJECT_ID('web.audit_field_change', 'U') IS NOT NULL DROP TABLE web.audit_field_change;
IF OBJECT_ID('web.audit_event', 'U') IS NOT NULL DROP TABLE web.audit_event;
IF OBJECT_ID('web.dim_audit_event_type', 'U') IS NOT NULL DROP TABLE web.dim_audit_event_type;
IF OBJECT_ID('web.account_progress_lock', 'U') IS NOT NULL DROP TABLE web.account_progress_lock;
IF OBJECT_ID('web.risk_exception', 'U') IS NOT NULL DROP TABLE web.risk_exception;
IF OBJECT_ID('web.dim_exception_status', 'U') IS NOT NULL DROP TABLE web.dim_exception_status;
IF OBJECT_ID('web.dim_application', 'U') IS NOT NULL DROP TABLE web.dim_application;
IF OBJECT_ID('web.identity_group_role_map', 'U') IS NOT NULL DROP TABLE web.identity_group_role_map;
IF OBJECT_ID('web.app_user', 'U') IS NOT NULL DROP TABLE web.app_user;
IF OBJECT_ID('web.role_permission', 'U') IS NOT NULL DROP TABLE web.role_permission;
IF OBJECT_ID('web.app_role', 'U') IS NOT NULL DROP TABLE web.app_role;
IF OBJECT_ID('web.app_permission', 'U') IS NOT NULL DROP TABLE web.app_permission;
IF OBJECT_ID('web.identity_provider_config', 'U') IS NOT NULL DROP TABLE web.identity_provider_config;
GO


/* ============================================================================
   2. AUTHENTICATION -- Design_Authentication_Architecture.md
   ============================================================================ */

-- identity_provider_config: one row per configured provider instance
-- (D-01, D-02, D-23 through D-27, D-38 through D-41). CreatedBy/ModifiedBy
-- are added as plain columns here and turned into FKs to web.app_user
-- further down, once app_user exists -- the two tables reference each
-- other, so the circular dependency is resolved with an ALTER after both
-- are created.
CREATE TABLE web.identity_provider_config (
    ProviderKey           INT IDENTITY(1,1) PRIMARY KEY,
    ProviderType          NVARCHAR(50)     NOT NULL,   -- WindowsIntegrated / OIDC / SAML / DevFakeAuth
    DisplayName           NVARCHAR(200)    NOT NULL,
    IsEnabled             BIT              NOT NULL DEFAULT 0,
    DisplayOrder          INT              NOT NULL DEFAULT 0,
    ConfigurationValues   NVARCHAR(MAX)    NULL,        -- non-secret settings as JSON; see design doc's own note that this is an implementation detail
    SecretReference       NVARCHAR(500)    NULL,        -- pointer into whichever Secrets Storage backend is active -- never the raw secret (Design_Secrets_Storage.md)
    CreatedDate           DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedBy             INT              NULL,        -- FK to web.app_user added below
    ModifiedBy            INT              NULL,        -- FK to web.app_user added below
    ModifiedDate          DATETIME2        NULL
);
GO


/* ============================================================================
   3. AUTHORIZATION -- Design_Authorization_Model.md
   ============================================================================ */

-- app_permission: the confirmed permission catalog (D-05, D-61). Unlike
-- app_role/role_permission below, this list itself is a confirmed decision,
-- not illustrative -- seeded accordingly.
CREATE TABLE web.app_permission (
    PermissionKey        INT IDENTITY(1,1) PRIMARY KEY,
    PermissionName       NVARCHAR(100)    NOT NULL UNIQUE,
    Description           NVARCHAR(500)    NULL
);

INSERT INTO web.app_permission (PermissionName, Description) VALUES
    ('ViewDashboard',              'View the dashboard/home page'),
    ('EditAccountProgress',        'Edit an account''s Blueprint progress record'),
    ('ApproveExceptions',          'Add or approve a risk exception'),
    ('ManageIdentityProviders',    'Configure authentication providers'),
    ('ManageGroupRoleMapping',     'Manage identity group to app role mappings'),
    ('CuratePlatformMapping',      'Curate platform_account_type_map'),
    ('ConfirmReconciliation',      'Confirm an account_reconciliation match'),
    ('ReloadRights',               'Trigger Reload Rights for another user''s session'),
    ('ManageRolesAndPermissions',  'Manage app_role/app_permission/role_permission definitions'),
    ('CurateApplicationMapping',   'Curate dim_application and dim_safe.ApplicationKey'),
    ('ManageSecretsStore',         'Configure the active Secrets Storage backend'),
    ('ManageFieldMetadata',        'Manage the Account Progress field-metadata list'),
    ('ViewAuditLog',               'View the audit log'),
    ('ManageApplicationConfiguration', 'Manage global application configuration (app_config)'),
    ('ViewDeploymentInfo',         'View deployment/environment info, health checks, and backup status');
GO

-- app_role: named permission bundles. Deliberately created empty --
-- Design_Authorization_Model.md's example bundles (Viewer/Analyst/Approver/
-- Admin) are explicitly flagged as "illustrative starting point, not a
-- fixed requirement -- confirm ... before these are built as the literal
-- default rows." Populate after that confirmation, not here.
CREATE TABLE web.app_role (
    AppRoleKey           INT IDENTITY(1,1) PRIMARY KEY,
    RoleName              NVARCHAR(100)    NOT NULL UNIQUE,
    Description            NVARCHAR(500)    NULL
);
GO

-- role_permission: many-to-many (D-05) -- one role can carry many
-- permissions, one permission can be granted through more than one role.
CREATE TABLE web.role_permission (
    RoleKey               INT NOT NULL REFERENCES web.app_role(AppRoleKey),
    PermissionKey          INT NOT NULL REFERENCES web.app_permission(PermissionKey),
    CONSTRAINT PK_role_permission PRIMARY KEY (RoleKey, PermissionKey)
);
GO

-- app_user (D-59): BlueTrack's own record of people who have logged into
-- the web app -- distinct from dbo.dim_user, which holds CyberArk vault
-- users pulled from Privilege Cloud/Self-Hosted exports.
CREATE TABLE web.app_user (
    UserKey               INT IDENTITY(1,1) PRIMARY KEY,
    ProviderKey            INT              NOT NULL REFERENCES web.identity_provider_config(ProviderKey),
    ExternalIdentifier     NVARCHAR(300)    NOT NULL,   -- Windows SID, OIDC sub/object ID, or SAML NameID
    DisplayName             NVARCHAR(300)    NULL,
    Email                    NVARCHAR(320)    NULL,
    FirstLogin                DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    LastLogin                 DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_app_user UNIQUE (ProviderKey, ExternalIdentifier)
);
GO

-- Now that web.app_user exists, wire up identity_provider_config's
-- CreatedBy/ModifiedBy -- the circular dependency this resolves is called
-- out in the table's own comment above. Guarded (not just the table drop
-- guard) since these constraints survive a table-level DROP/CREATE of
-- identity_provider_config only when this ALTER re-runs too.
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_identity_provider_config_CreatedBy')
BEGIN
    ALTER TABLE web.identity_provider_config
        ADD CONSTRAINT FK_identity_provider_config_CreatedBy FOREIGN KEY (CreatedBy) REFERENCES web.app_user(UserKey);
END
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_identity_provider_config_ModifiedBy')
BEGIN
    ALTER TABLE web.identity_provider_config
        ADD CONSTRAINT FK_identity_provider_config_ModifiedBy FOREIGN KEY (ModifiedBy) REFERENCES web.app_user(UserKey);
END
GO

-- identity_group_role_map: scoped per provider, since a group name can mean
-- different things across providers (D-03, D-04, D-05, D-13, D-14).
CREATE TABLE web.identity_group_role_map (
    MappingKey            INT IDENTITY(1,1) PRIMARY KEY,
    ProviderKey            INT              NOT NULL REFERENCES web.identity_provider_config(ProviderKey),
    IdentityGroupName       NVARCHAR(300)    NOT NULL,
    AppRoleKey                INT              NOT NULL REFERENCES web.app_role(AppRoleKey),
    CONSTRAINT UQ_identity_group_role_map UNIQUE (ProviderKey, IdentityGroupName, AppRoleKey)
);
GO


/* ============================================================================
   4. RISK EXCEPTION TRACKING -- Design_Risk_Exception_Tracking.md
   ============================================================================ */

-- dim_application (D-18, D-25/Q-25, D-31, D-44, D-46): a curated business
-- grouping above dim_safe, not present in any CyberArk export. Created
-- empty and populated as a curated, manually-reviewed mapping -- same
-- pattern as dbo.platform_account_type_map -- not inferred from Safe names.
CREATE TABLE web.dim_application (
    ApplicationKey        INT IDENTITY(1,1) PRIMARY KEY,
    ApplicationGUID        UNIQUEIDENTIFIER NOT NULL UNIQUE DEFAULT NEWID(),
    ApplicationCode          NVARCHAR(50)     NOT NULL UNIQUE,
    ApplicationName            NVARCHAR(300)    NOT NULL UNIQUE,
    Description                  NVARCHAR(1000)   NULL,
    OwnerName                      NVARCHAR(300)    NULL,
    OwnerEmail                       NVARCHAR(320)    NULL,
    TechnicalName                      NVARCHAR(300)    NULL,
    TechnicalEmail                        NVARCHAR(320)    NULL,
    Notes                                    NVARCHAR(2000)   NULL
);
GO

-- Resolved relationship (D-31): a Safe belongs to exactly one Application;
-- an Application can own many Safes. Nullable because system/built-in
-- Safes (e.g. 'VaultInternal', 'Notification Engine' -- seen in the actual
-- Privilege Cloud export sample) have no application owner. Column and FK
-- constraint are guarded independently (not one combined statement) so a
-- re-run can re-add just the constraint after cleanup dropped it (1a)
-- without erroring on "column already exists".
IF COL_LENGTH('dbo.dim_safe', 'ApplicationKey') IS NULL
BEGIN
    ALTER TABLE dbo.dim_safe ADD ApplicationKey INT NULL;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_dim_safe_ApplicationKey')
BEGIN
    ALTER TABLE dbo.dim_safe ADD CONSTRAINT FK_dim_safe_ApplicationKey FOREIGN KEY (ApplicationKey) REFERENCES web.dim_application(ApplicationKey);
END
GO

-- dim_exception_status: confirmed values (not illustrative), seeded accordingly.
CREATE TABLE web.dim_exception_status (
    ExceptionStatusKey    INT IDENTITY(1,1) PRIMARY KEY,
    StatusName              NVARCHAR(50)     NOT NULL UNIQUE
);

INSERT INTO web.dim_exception_status (StatusName) VALUES
    ('Active'), ('Expired'), ('Revoked');
GO

-- risk_exception (D-07, D-17, D-18, D-19, D-31, D-59). Exactly one of
-- AccountKey/ApplicationKey must be set per exception -- deliberately NOT a
-- CHECK constraint here: the design doc calls for this enforced at the
-- application layer, consistent with how this project avoids
-- database-level enforcement of business rules elsewhere (see
-- Design_Risk_Exception_Tracking.md).
CREATE TABLE web.risk_exception (
    ExceptionKey          INT IDENTITY(1,1) PRIMARY KEY,
    ExceptionID             NVARCHAR(50)     NOT NULL UNIQUE,   -- flexible/org-configurable numbering scheme (D-17), e.g. EXC-2026-0001
    AccountKey                BIGINT           NULL REFERENCES dbo.fact_account(AccountKey),
    ApplicationKey               INT              NULL REFERENCES web.dim_application(ApplicationKey),
    Justification                   NVARCHAR(2000)   NOT NULL,
    ApprovedBy                        INT              NOT NULL REFERENCES web.app_user(UserKey),
    ApprovalDate                        DATE             NOT NULL,
    ReviewDate                            DATE             NOT NULL,
    ExceptionStatusKey                       INT              NOT NULL REFERENCES web.dim_exception_status(ExceptionStatusKey),
    ExternalTicketReference                     NVARCHAR(200)    NULL
);
GO

-- fact_account_progress.ExceptionKey: points to the currently-active
-- risk_exception row, populated when CurrentStatusKey = Risk Accepted /
-- Excluded. Column and FK constraint guarded independently -- same reason
-- as dim_safe.ApplicationKey above.
IF COL_LENGTH('dbo.fact_account_progress', 'ExceptionKey') IS NULL
BEGIN
    ALTER TABLE dbo.fact_account_progress ADD ExceptionKey INT NULL;
END
GO
IF NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'FK_fact_account_progress_ExceptionKey')
BEGIN
    ALTER TABLE dbo.fact_account_progress ADD CONSTRAINT FK_fact_account_progress_ExceptionKey FOREIGN KEY (ExceptionKey) REFERENCES web.risk_exception(ExceptionKey);
END
GO


/* ============================================================================
   5. DATA & EDITING BEHAVIOR -- Design_Data_Editing_Behavior.md
   ============================================================================ */

-- account_progress_lock (D-50): pessimistic locking. Separate from
-- fact_account_progress itself -- lock state is transient session data,
-- not business data.
CREATE TABLE web.account_progress_lock (
    AccountKey            BIGINT           NOT NULL PRIMARY KEY REFERENCES dbo.fact_account(AccountKey),
    LockedByUserKey         INT              NOT NULL REFERENCES web.app_user(UserKey),
    LockedAt                   DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    LastHeartbeatAt               DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME()
);
GO


/* ============================================================================
   6. AUDIT LOGGING -- Design_Audit_Logging.md
   ============================================================================ */

-- dim_audit_event_type: illustrative example set from the design doc,
-- seeded as a reasonable starting point -- extend as new event types come up.
CREATE TABLE web.dim_audit_event_type (
    AuditEventTypeKey     INT IDENTITY(1,1) PRIMARY KEY,
    EventTypeName            NVARCHAR(100)    NOT NULL UNIQUE,
    Description                 NVARCHAR(500)    NULL
);

INSERT INTO web.dim_audit_event_type (EventTypeName, Description) VALUES
    ('Logon',               'Successful application logon'),
    ('LogonFailed',          'Failed application logon attempt'),
    ('FieldEdit',             'A governed field was changed'),
    ('ExceptionApproved',      'A risk exception was approved'),
    ('ProviderConfigChanged',    'An identity provider''s configuration changed'),
    ('ReloadRights',                'A Reload Rights action was triggered');
GO

-- audit_event (D-10, D-11, D-51 Reason, D-59 app_user). Field-level diffs
-- for edits are captured in audit_field_change below, not inline here.
CREATE TABLE web.audit_event (
    AuditEventKey          BIGINT IDENTITY(1,1) PRIMARY KEY,
    AuditEventTypeKey        INT              NOT NULL REFERENCES web.dim_audit_event_type(AuditEventTypeKey),
    OccurredAt                  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    PerformedByUserKey             INT              NOT NULL REFERENCES web.app_user(UserKey),
    EntityName                        NVARCHAR(200)    NULL,        -- e.g. 'fact_account_progress', 'risk_exception'
    EntityKey                            NVARCHAR(100)    NULL,
    SourceIpAddress                         NVARCHAR(45)     NULL,        -- long enough for an IPv6 address
    Detail                                     NVARCHAR(2000)   NULL,
    Reason                                        NVARCHAR(1000)   NULL         -- structured justification, e.g. a Blueprint stage regression (D-51)
);
GO

-- audit_field_change: one row per changed field, linked to the parent event.
CREATE TABLE web.audit_field_change (
    AuditFieldChangeKey    BIGINT IDENTITY(1,1) PRIMARY KEY,
    AuditEventKey             BIGINT           NOT NULL REFERENCES web.audit_event(AuditEventKey),
    FieldName                    NVARCHAR(200)    NOT NULL,
    OldValue                        NVARCHAR(MAX)    NULL,
    NewValue                           NVARCHAR(MAX)    NULL
);
GO

-- audit_purge_log (D-62): mirrors dbo.import_log's shape for the retention
-- purge's own run history, rather than logging the purge recursively into
-- audit_event itself.
CREATE TABLE web.audit_purge_log (
    PurgeBatchId           UNIQUEIDENTIFIER NOT NULL PRIMARY KEY DEFAULT NEWID(),
    CutoffDate                DATE             NOT NULL,
    RowsPurged                    INT              NULL,
    StartedAt                        DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    CompletedAt                          DATETIME2        NULL,
    Status                                   NVARCHAR(50)     NOT NULL DEFAULT 'Started',
    ErrorMessage                                NVARCHAR(2000)   NULL
);
GO

-- audit_config (D-12, D-35): a single settings row for audit-specific
-- configuration. RetentionDays is deliberately left NULL here rather than
-- guessing a default -- no specific value was ever decided (only that it
-- must be admin-configurable), so it must be set explicitly before the
-- purge job (usp_PurgeAuditLog, D-62) is scheduled to run meaningfully.
CREATE TABLE web.audit_config (
    AuditConfigKey         INT IDENTITY(1,1) PRIMARY KEY,
    RetentionDays             INT              NULL,
    LogReadEvents                 BIT              NOT NULL DEFAULT 0,
    ModifiedBy                       INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedDate                        DATETIME2        NULL
);

INSERT INTO web.audit_config (RetentionDays, LogReadEvents) VALUES (NULL, 0);
GO


/* ============================================================================
   7. APPLICATION STRUCTURE -- Design_Application_Structure.md
   ============================================================================ */

-- app_config (D-60): general global settings, kept separate from
-- audit_config so audit-specific and general settings don't mix. Step-up
-- MFA scope (D-29) is deliberately NOT a column here -- it's treated as a
-- fixed code-level policy, not a runtime setting (see the design doc's own
-- note on this; revisit if that reading turns out to be wrong).
CREATE TABLE web.app_config (
    AppConfigKey           INT IDENTITY(1,1) PRIMARY KEY,
    IdleTimeoutMinutes         INT              NOT NULL DEFAULT 30,
    BreadcrumbPosition            NVARCHAR(20)     NOT NULL DEFAULT 'TopLeft',
    ModifiedBy                       INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedDate                        DATETIME2        NULL
);

INSERT INTO web.app_config (IdleTimeoutMinutes, BreadcrumbPosition) VALUES (30, 'TopLeft');
GO


/* ============================================================================
   8. INTERFACE EXTENSIBILITY -- Design_Interface_Extensibility.md
   ============================================================================ */

-- account_progress_field_metadata: the field-metadata-driven pattern's
-- central field-definition list. Created empty and populated as governed
-- fact_account_progress fields are exposed through it -- see the design
-- doc's own field-by-field description.
CREATE TABLE web.account_progress_field_metadata (
    FieldMetadataKey       INT IDENTITY(1,1) PRIMARY KEY,
    FieldName                 NVARCHAR(200)    NOT NULL UNIQUE,
    DisplayLabel                 NVARCHAR(200)    NOT NULL,
    FieldType                       NVARCHAR(50)     NOT NULL,
    ReferenceTable                     NVARCHAR(200)    NULL,
    IsRequired                            BIT              NOT NULL DEFAULT 0,
    RequiredPermission                       INT              NULL REFERENCES web.app_permission(PermissionKey),
    DisplayOrder                                INT              NOT NULL DEFAULT 0
);
GO
