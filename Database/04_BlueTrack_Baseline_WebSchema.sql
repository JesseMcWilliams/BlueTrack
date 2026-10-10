/* ============================================================================
   04_BlueTrack_Baseline_WebSchema.sql

   BASELINE (D-195, 2026-10-09). One of the six scripts that replaced the
   numbered scripts 01-51 before the first release. It builds the schema those
   scripts left behind, in final form: later columns and constraints are part
   of each CREATE TABLE, and each procedure, view and function appears once,
   as its last version. The history of every change is in git and in
   Claude_Docs/Design_Decision-Register.md.

   Runs only against an EMPTY database (App/Migrator gives it one). A database
   built from the old scripts has these baseline scripts marked as applied by
   App/Migrator instead of running them (see its header).

   The web schema (D-64): every table behind the web interface --
   authentication, authorization, risk exceptions, audit logging, settings,
   secrets store, session cache, preferences, credentials/LDAP,
   notifications, risk scoring, AD account discovery, data feeds and account
   deletion -- with the reference rows the design confirmed. A separate
   schema, not a separate database, because several tables have real foreign
   keys into dbo (SQL Server can't enforce them across databases). Also adds
   the two dbo columns that point into web (dim_safe.ApplicationKey,
   fact_account_progress.ExceptionKey).
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
GO


/* ============================================================================
   2. AUTHENTICATION -- Design_Authentication-Architecture.md
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
    SecretReference       NVARCHAR(500)    NULL,        -- pointer into whichever Secrets Storage backend is active -- never the raw secret (Design_Secrets-Storage.md)
    CreatedDate           DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    CreatedBy             INT              NULL,        -- FK to web.app_user added below
    ModifiedBy            INT              NULL,        -- FK to web.app_user added below
    ModifiedDate          DATETIME2        NULL
);
GO


/* ============================================================================
   3. AUTHORIZATION -- Design_Authorization-Model.md
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
    ('ViewDeploymentInfo',         'View deployment/environment info, health checks, and backup status'),
    ('ManageNotifications',        'Configure SMTP settings and notification recipients'),
    ('ManageCredentials',          'Manage stored credentials (SMTP, LDAP bind account, etc.) and LDAP configuration'),
    ('TriggerBackup',              'Trigger an on-demand database and configuration backup from the Deployment admin page'),
    ('ManageTargets',              'Manage the Target inventory (servers, databases, applications, etc.) for risk scoring'),
    ('ManageAccessGroups',         'Manage Access Groups (privileged-access groups in the managed environment) for risk scoring'),
    ('ViewRiskReport',             'View the computed account risk score report'),
    ('ManageRiskScoreBands',       'Manage the named bands that translate a computed risk score into a label'),
    ('ViewDiscoveredAccounts',     'View the Discovered Accounts report (AD accounts not yet onboarded into CyberArk)'),
    ('ManageDiscoveredAccounts',   'Accept a Discovered Account into onboarding tracking, or dismiss it'),
    ('ViewRiskExceptionSodReport', 'View the Risk Exception segregation-of-duties report (same person approved and linked an exception)'),
    ('ManageDataSources',          'Manage data feeds: add, edit, test and run scheduled CSV imports, and view their history'),
    ('DeleteAccounts',             'Delete and undelete accounts in BlueTrack (a reason is always required)');
GO

-- app_role: named permission bundles. Deliberately created empty --
-- Design_Authorization-Model.md's example bundles (Viewer/Analyst/Approver/
-- Admin) are explicitly flagged as "illustrative starting point, not a
-- fixed requirement -- confirm ... before these are built as the literal
-- default rows." Populate after that confirmation, not here.
CREATE TABLE web.app_role (
    AppRoleKey           INT IDENTITY(1,1) PRIMARY KEY,
    RoleName              NVARCHAR(100)    NOT NULL UNIQUE,
    Description            NVARCHAR(500)    NULL,
    NotificationEmail      NVARCHAR(320)    NULL    -- per-role notification email (Design_Notifications.md)
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
   4. RISK EXCEPTION TRACKING -- Design_Risk-Exception-Tracking.md
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
-- Design_Risk-Exception-Tracking.md).
CREATE TABLE web.risk_exception (
    ExceptionKey          INT IDENTITY(1,1) PRIMARY KEY,
    ExceptionID             NVARCHAR(50)     NOT NULL UNIQUE,   -- flexible/org-configurable numbering scheme (D-17), e.g. EXC-2026-0001
    AccountKey                BIGINT           NULL REFERENCES dbo.fact_account(AccountKey),
    ApplicationKey               INT              NULL REFERENCES web.dim_application(ApplicationKey),
    Justification                   NVARCHAR(2000)   NOT NULL,
    ApprovedBy                        INT              NULL REFERENCES web.app_user(UserKey),   -- NULL for an imported exception (D-183), which names its approver in ApprovedByName
    ApprovalDate                        DATE             NOT NULL,
    ReviewDate                            DATE             NOT NULL,
    ExceptionStatusKey                       INT              NOT NULL REFERENCES web.dim_exception_status(ExceptionStatusKey),
    ExternalTicketReference                     NVARCHAR(200)    NULL,
    -- D-183: an exception approved in another tool and imported.
    ApprovedByName          NVARCHAR(200)    NULL,    -- the approver's name in the source tool
    SourceTool              NVARCHAR(100)    NULL,    -- e.g. 'ServiceNow GRC'
    SourceExceptionId       NVARCHAR(100)    NULL,    -- its ID in that tool, unique per tool
    SourceUrl               NVARCHAR(1000)   NULL,
    ImportedBy              INT              NULL CONSTRAINT FK_risk_exception_ImportedBy REFERENCES web.app_user(UserKey),
    ImportedDate            DATETIME2        NULL,
    CONSTRAINT CK_risk_exception_Approver CHECK (ApprovedBy IS NOT NULL OR ApprovedByName IS NOT NULL)
);

-- A repeat import of the same exception is refused by this index (D-183).
CREATE UNIQUE INDEX UX_risk_exception_Source ON web.risk_exception (SourceTool, SourceExceptionId)
WHERE SourceExceptionId IS NOT NULL;
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
   5. DATA & EDITING BEHAVIOR -- Design_Data-Editing-Behavior.md
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
   6. AUDIT LOGGING -- Design_Audit-Logging.md

   dim_audit_event_type's seed list folds in, directly, the two event types
   originally added by old 13_BlueTrack_AuditEventTypes.sql
   (ExceptionReviewExtended/ExceptionRevoked, found missing while wiring
   real audit logging into the Risk Exceptions actions -- create/
   extend-review/revoke -- since the original catalog only covered
   creation) and the one added by old 21_BlueTrack_ReadEventType.sql
   (RecordViewed, needed once D-35's LogReadEvents enforcement (D-83) had
   an event to log against). This is still an illustrative starting set,
   per the design doc -- extend as new event types come up.
   ============================================================================ */

CREATE TABLE web.dim_audit_event_type (
    AuditEventTypeKey     INT IDENTITY(1,1) PRIMARY KEY,
    EventTypeName            NVARCHAR(100)    NOT NULL UNIQUE,
    Description                 NVARCHAR(500)    NULL
);

INSERT INTO web.dim_audit_event_type (EventTypeName, Description) VALUES
    ('Logon',                    'Successful application logon'),
    ('LogonFailed',               'Failed application logon attempt'),
    ('FieldEdit',                  'A governed field was changed'),
    ('ExceptionApproved',           'A risk exception was approved'),
    ('ProviderConfigChanged',        'An identity provider''s configuration changed'),
    ('ReloadRights',                   'A Reload Rights action was triggered'),
    ('ExceptionReviewExtended',           'A risk exception''s ReviewDate was extended (re-approval)'),
    ('ExceptionRevoked',                    'A risk exception was revoked'),
    ('RecordViewed',                           'A governed record''s detail view was read (only logged when audit_config.LogReadEvents is enabled)'),
    ('BulkEdit',                 'A bulk edit changed several records at once (each change is also logged as its own FieldEdit)'),
    ('ExceptionImported',        'A risk exception approved in another tool was imported'),
    ('AccountDeleted',           'An account was deleted in BlueTrack (with a reason)'),
    ('AccountUndeleted',         'An account deleted in BlueTrack was restored (with a reason)');
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
   7. APPLICATION STRUCTURE -- Design_Application-Structure.md

   app_config folds in, directly, the columns originally added by old
   12_BlueTrack_ExceptionIdNumbering.sql (ExceptionIdPattern/
   ExceptionIdSequenceYear/ExceptionIdNextSequence -- D-17's admin-configurable
   ExceptionID numbering scheme, parsed by App/Api/Data/ExceptionIdGenerator.cs)
   and old 15_BlueTrack_LockTimeoutConfig.sql (LockTimeoutMinutes -- D-50's
   admin-configurable abandoned-lock timeout, default 5 matching the design
   doc's own stated default).
   ============================================================================ */

-- app_config (D-60): general global settings, kept separate from
-- audit_config so audit-specific and general settings don't mix. Step-up
-- MFA scope (D-29) is deliberately NOT a column here -- it's treated as a
-- fixed code-level policy, not a runtime setting (see the design doc's own
-- note on this; revisit if that reading turns out to be wrong).
CREATE TABLE web.app_config (
    AppConfigKey              INT              IDENTITY(1,1) PRIMARY KEY,
    IdleTimeoutMinutes            INT              NOT NULL DEFAULT 30,
    BreadcrumbPosition               NVARCHAR(20)     NOT NULL DEFAULT 'TopLeft',
    ModifiedBy                          INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedDate                           DATETIME2        NULL,
    ExceptionIdPattern                        NVARCHAR(50)     NOT NULL DEFAULT 'EXC-{yyyy}-{seq:0000}',   -- D-17; tokens parsed by App/Api/Data/ExceptionIdGenerator.cs: {yyyy}, {yy}, {seq:0000}
    ExceptionIdSequenceYear                       INT              NULL,                                       -- D-17; the running counter resets to 1 whenever the current year no longer matches this
    ExceptionIdNextSequence                          INT              NOT NULL DEFAULT 1,                          -- D-17
    LockTimeoutMinutes                                  INT              NOT NULL DEFAULT 5,                         -- D-50
    BackupFolder            NVARCHAR(500)    NULL,    -- destination of the Deployment page's "Backup App" button
    ActiveRiskAlgorithm     NVARCHAR(50)     NOT NULL CONSTRAINT DF_app_config_ActiveRiskAlgorithm DEFAULT 'DominantPlusTail',   -- D-119
    EnforceRiskExceptionSegregationOfDuties BIT NOT NULL CONSTRAINT DF_app_config_EnforceRiskExceptionSoD DEFAULT (0),
    -- D-181: data feeds' nightly run time and run-history retention, and the
    -- business hours inside which Run now warns. BusinessDays is
    -- comma-separated three-letter English day names.
    DataFeedRunTime         TIME(0)          NOT NULL CONSTRAINT DF_app_config_DataFeedRunTime DEFAULT '04:00',
    DataFeedRunRetentionDays INT             NOT NULL CONSTRAINT DF_app_config_DataFeedRunRetentionDays DEFAULT 15,
    BusinessHoursStart      TIME(0)          NOT NULL CONSTRAINT DF_app_config_BusinessHoursStart DEFAULT '07:00',
    BusinessHoursEnd        TIME(0)          NOT NULL CONSTRAINT DF_app_config_BusinessHoursEnd DEFAULT '18:00',
    BusinessDays            NVARCHAR(30)     NOT NULL CONSTRAINT DF_app_config_BusinessDays DEFAULT 'Mon,Tue,Wed,Thu,Fri',
    BulkEditMaxAccounts     INT              NOT NULL CONSTRAINT DF_app_config_BulkEditMaxAccounts DEFAULT 500,   -- D-182
    -- D-186: name patterns (Off / Prefix / Suffix / Regex) marking decommissioning
    -- safes and accounts, and safes the import ignores.
    SafeDecomMode     NVARCHAR(10)  NOT NULL CONSTRAINT DF_app_config_SafeDecomMode DEFAULT 'Off'
                      CONSTRAINT CK_app_config_SafeDecomMode CHECK (SafeDecomMode IN ('Off', 'Prefix', 'Suffix', 'Regex')),
    SafeDecomValue    NVARCHAR(200) NULL,
    AccountDecomMode  NVARCHAR(10)  NOT NULL CONSTRAINT DF_app_config_AccountDecomMode DEFAULT 'Off'
                      CONSTRAINT CK_app_config_AccountDecomMode CHECK (AccountDecomMode IN ('Off', 'Prefix', 'Suffix', 'Regex')),
    AccountDecomValue NVARCHAR(200) NULL,
    SafeIgnoreMode    NVARCHAR(10)  NOT NULL CONSTRAINT DF_app_config_SafeIgnoreMode DEFAULT 'Off'
                      CONSTRAINT CK_app_config_SafeIgnoreMode CHECK (SafeIgnoreMode IN ('Off', 'Prefix', 'Suffix', 'Regex')),
    SafeIgnoreValue   NVARCHAR(200) NULL
);

INSERT INTO web.app_config (IdleTimeoutMinutes, BreadcrumbPosition, ExceptionIdPattern, ExceptionIdSequenceYear, ExceptionIdNextSequence, LockTimeoutMinutes)
VALUES (30, 'TopLeft', 'EXC-{yyyy}-{seq:0000}', NULL, 1, 5);
GO


/* ============================================================================
   8. INTERFACE EXTENSIBILITY -- Design_Interface-Extensibility.md
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


/* ============================================================================
   9. SECRETS STORE -- Design_Secrets-Storage.md

   Folded in 2026-09-05 from old 14_BlueTrack_SecretsStoreSchema.sql. This
   is the config *record* only (which backend is active, plus its
   non-secret settings) -- it does not implement any actual backend
   (Windows DPAPI, CyberArk CP, etc.); those remain unbuilt, consistent
   with AuthenticationExtensions.cs's own note that only WindowsIntegrated
   authentication is wired so far.

   "Exactly one active backend at a time" (per the design doc) is enforced
   at the application layer (SecretsStoreRepository), not a database
   constraint -- consistent with how this project avoids triggers/CHECK
   constraints for business rules elsewhere (e.g. risk_exception's
   AccountKey/ApplicationKey exclusivity).
   ============================================================================ */

CREATE TABLE web.secrets_store (
    SecretStoreKey     INT IDENTITY(1,1) PRIMARY KEY,
    BackendType         NVARCHAR(50)     NOT NULL UNIQUE,   -- AzureKeyVault / AwsSecretsManager / WindowsDpapi / CyberArkCCP / CyberArkCP / CyberArkConjur
    IsActive             BIT              NOT NULL DEFAULT 0,
    BackendSettings         NVARCHAR(MAX)    NULL             -- non-secret backend-specific settings as JSON (e.g. Key Vault URI)
);

INSERT INTO web.secrets_store (BackendType, IsActive, BackendSettings) VALUES
    ('WindowsDpapi',      1, NULL),   -- first backend built overall (D-36) -- seeded active by default
    ('CyberArkCP',        0, NULL),   -- designated first CyberArk backend (D-32)
    ('AzureKeyVault',     0, NULL),
    ('AwsSecretsManager', 0, NULL),
    ('CyberArkCCP',       0, NULL),
    ('CyberArkConjur',    0, NULL);
GO


/* ============================================================================
   10. SESSION CACHE -- Design_Session_And_Rights_Caching.md

   Folded in 2026-09-05 from old 20_BlueTrack_SessionCacheSchema.sql (D-82).
   A SQL Server-backed distributed cache (Microsoft.Extensions.Caching.SqlServer)
   was chosen over Redis, since no such infrastructure exists in this
   environment yet and SQL Server is already the one confirmed, reachable
   piece of shared infrastructure. This is the exact table shape
   Microsoft.Extensions.Caching.SqlServer requires -- confirmed by actually
   running the real `dotnet-sql-cache create` tool (installed via
   `dotnet tool install --global dotnet-sql-cache`) against this database
   and reading back what it really created via INFORMATION_SCHEMA, not
   copied from documentation. The only difference from the tool's own
   output: an explicit PRIMARY KEY constraint name (the tool leaves it
   system-generated), for consistency with this project's convention of
   naming its own constraints.

   "Session" here is BlueTrack's cached-rights-per-identity concept (see
   App/Api/Auth/UserRightsCache.cs), not an ASP.NET Core cookie-based
   Session -- Windows Negotiate doesn't need cookie-based session tracking
   for anything else the app does, so no cookie/session-ID machinery was
   introduced on top of this cache.
   ============================================================================ */

CREATE TABLE web.distributed_cache (
    Id                             NVARCHAR(449)     NOT NULL,
    Value                            VARBINARY(MAX)    NOT NULL,
    ExpiresAtTime                      DATETIMEOFFSET    NOT NULL,
    SlidingExpirationInSeconds            BIGINT            NULL,
    AbsoluteExpiration                       DATETIMEOFFSET    NULL,
    CONSTRAINT PK_distributed_cache PRIMARY KEY CLUSTERED (Id)
);

CREATE NONCLUSTERED INDEX Index_ExpiresAtTime ON web.distributed_cache (ExpiresAtTime);
GO


/* ============================================================================
   11. USER PREFERENCES -- Design_Accessibility-And-Theming.md

   Folded in 2026-09-05 from old 23_BlueTrack_UserPreferenceSchema.sql
   (D-93). A generalized per-user preferences store, keyed by an arbitrary
   PreferenceKey rather than one column per setting -- the user's explicit
   choice, so a future preference beyond the first one (Theme) doesn't need
   its own schema migration. Composite PK (UserKey, PreferenceKey): exactly
   one value per preference per user, upserted by the API rather than
   enforced by a database trigger.
   ============================================================================ */

CREATE TABLE web.user_preference (
    UserKey           INT              NOT NULL REFERENCES web.app_user(UserKey),
    PreferenceKey      NVARCHAR(50)     NOT NULL,
    PreferenceValue     NVARCHAR(200)    NOT NULL,
    ModifiedDate          DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT PK_user_preference PRIMARY KEY (UserKey, PreferenceKey)
);
GO



/* ============================================================================
   12. REPORTING VIEW -- application-scoped exception coverage
   (Design_Risk-Exception-Tracking.md, D-81)

   Folded in 2026-09-05 from old 19_BlueTrack_ApplicationExceptionView.sql.
   Application-scoped exceptions were explicitly left with an undecided
   propagation mechanism -- "a view vs. a batch update... not decided
   here." Resolved directly by the user, 2026-09-04: a view. Computes "is
   this account currently covered by an application-scoped exception" live,
   at query time, through fact_account.SafeKey -> dim_safe.ApplicationKey ->
   web.risk_exception, rather than writing fact_account_progress.ExceptionKey
   for every account under the application (which D-77 only does for the
   account-scoped case).

   Deliberately does NOT write to fact_account_progress.ExceptionKey --
   that column stays exactly what D-77 already made it (the account-scoped
   pointer). This view is a second, independent source of "is this account
   excepted," not a replacement for that column. Anything that needs the
   full picture (is this account covered by ANY exception, account- or
   application-scoped) needs to check both.

   Can return more than one row per account if more than one Active
   application-scoped exception exists for the same Application -- nothing
   in the app prevents creating two, so this is a live computation, not an
   assumption of exactly one.
   ============================================================================ */

CREATE OR ALTER VIEW web.vw_account_application_exception AS
SELECT
    fa.AccountKey,
    re.ExceptionKey,
    re.ExceptionID,
    re.ApplicationKey,
    da.ApplicationName,
    re.ReviewDate
FROM dbo.fact_account fa
JOIN dbo.dim_safe ds           ON ds.SafeKey = fa.SafeKey
JOIN web.dim_application da     ON da.ApplicationKey = ds.ApplicationKey
JOIN web.risk_exception re       ON re.ApplicationKey = da.ApplicationKey
JOIN web.dim_exception_status des ON des.ExceptionStatusKey = re.ExceptionStatusKey
WHERE des.StatusName = 'Active'
  AND fa.IsDeleted = 0;
GO

/* ============================================================================
   13. CREDENTIALS, LDAP AND NOTIFICATIONS -- Design_Credentials-Management.md,
       Design_Notifications.md
   ============================================================================ */

-- web.credential: a generic named-credential store (D-115/D-116). DPAPI rows
-- hold Username/ProtectedPassword; vault-backed rows hold the
-- Safe/Folder/Object query shape (CyberArk CP/CCP/Conjur, and with different
-- field meanings, Azure Key Vault / AWS Secrets Manager). All nullable so
-- exactly one set applies per row.
CREATE TABLE web.credential (
    CredentialKey        INT IDENTITY(1,1) PRIMARY KEY,
    CredentialName          NVARCHAR(100)    NOT NULL UNIQUE,
    BackendType                NVARCHAR(50)     NOT NULL,   -- 'WindowsDpapi' | 'CyberArkCP' | 'CyberArkCCP' | 'CyberArkConjur' | 'AzureKeyVault' | 'AwsSecretsManager'
    Username                      NVARCHAR(255)    NULL,       -- DPAPI only
    ProtectedPassword                NVARCHAR(2000)   NULL,       -- DPAPI only -- ciphertext
    ScopePreference                     NVARCHAR(10)     NULL,       -- DPAPI only -- 'Machine' | 'User', the admin's choice
    CurrentScope                           NVARCHAR(10)     NULL,       -- DPAPI only -- 'Machine' | 'User', what ProtectedPassword is actually protected with right now
    VaultSafe                                 NVARCHAR(100)    NULL,       -- vault backends only
    VaultFolder                                  NVARCHAR(100)    NULL,       -- vault backends only
    VaultObject                                     NVARCHAR(200)    NULL,       -- vault backends only
    ModifiedBy                                         INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedDate                                           DATETIME2        NULL
);
GO

-- web.ldap_config: one row per AD domain (D-116; multi-domain since
-- 2026-09-16). CredentialKey points at the bind account; LDAP lookups are
-- skipped until IsEnabled and CredentialKey (or UseTrustedConnection, which
-- binds as the app pool's own identity) are set. Seeded with one disabled
-- row for the 'Default' domain.
CREATE TABLE web.ldap_config (
    LdapConfigKey     INT IDENTITY(1,1) PRIMARY KEY,
    IsEnabled            BIT              NOT NULL DEFAULT 0,
    DomainController        NVARCHAR(255)    NULL,
    SearchBase                  NVARCHAR(500)    NULL,
    UseSsl                          BIT              NOT NULL DEFAULT 0,
    CredentialKey                      INT              NULL REFERENCES web.credential(CredentialKey),
    ModifiedBy                             INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedDate                               DATETIME2        NULL,
    UseTrustedConnection   BIT             NOT NULL CONSTRAINT DF_ldap_config_UseTrustedConnection DEFAULT 0,
    DomainName             NVARCHAR(100)   NOT NULL CONSTRAINT DF_ldap_config_DomainName DEFAULT 'Default',
    CONSTRAINT UQ_ldap_config_DomainName UNIQUE (DomainName)
);

INSERT INTO web.ldap_config (IsEnabled, UseSsl) VALUES (0, 0);
GO

-- notification_config: singleton SMTP settings (D-115). The SMTP login is a
-- web.credential row (SmtpCredentialKey). Seeded with STARTTLS defaults and no
-- server -- an admin fills it in on the Notifications page. The two TLS
-- overrides (ignore CRL/OCSP failures; ignore all SSL errors) default off.
CREATE TABLE web.notification_config (
    NotificationConfigKey     INT IDENTITY(1,1) PRIMARY KEY,
    SmtpHost                    NVARCHAR(255)    NULL,
    SmtpPort                      INT              NOT NULL DEFAULT 587,
    EnableStartTls                  BIT              NOT NULL DEFAULT 1,
    AuthMethod                        NVARCHAR(20)     NOT NULL DEFAULT 'Basic',   -- 'None' or 'Basic' -- see Design_Notifications.md's OAuth2 open question (resolved: out of scope for now)
    FromAddress                              NVARCHAR(320)    NULL,
    FromDisplayName                            NVARCHAR(255)    NULL,
    ModifiedBy                                    INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedDate                                    DATETIME2        NULL,
    SmtpCredentialKey      INT              NULL REFERENCES web.credential(CredentialKey),
    IgnoreCrlErrors        BIT              NOT NULL CONSTRAINT DF_notification_config_IgnoreCrlErrors DEFAULT 0,
    IgnoreSslErrors        BIT              NOT NULL CONSTRAINT DF_notification_config_IgnoreSslErrors DEFAULT 0
);

INSERT INTO web.notification_config (SmtpPort, EnableStartTls, AuthMethod) VALUES (587, 1, 'Basic');
GO

-- notification_recipient: independent of role membership. Created empty --
-- an admin adds recipients on the Notifications page.
CREATE TABLE web.notification_recipient (
    RecipientKey     INT IDENTITY(1,1) PRIMARY KEY,
    Email              NVARCHAR(320)    NOT NULL UNIQUE,
    DisplayName          NVARCHAR(255)    NULL,
    IsActive                BIT              NOT NULL DEFAULT 1
);
GO

-- dim_notification_type: extensible catalog -- a new alert kind is a new row.
-- TargetRoleKey optionally also sends it to a role's NotificationEmail.
CREATE TABLE web.dim_notification_type (
    NotificationTypeKey     INT IDENTITY(1,1) PRIMARY KEY,
    NotificationTypeName      NVARCHAR(100)    NOT NULL UNIQUE,
    Description                  NVARCHAR(500)    NULL,
    TargetRoleKey          INT              NULL REFERENCES web.app_role(AppRoleKey)
);

INSERT INTO web.dim_notification_type (NotificationTypeName, Description) VALUES
    ('DevFakeAuthEnabledTooLong', 'DevFakeAuth has been enabled for more than 7 days (D-114) -- it bypasses real authentication and should only be left on briefly during local development.');
GO

-- notification_log: one row per send -- both an audit trail and the
-- de-duplication mechanism (a recent row for the same type within its
-- cooldown window means no re-send).
CREATE TABLE web.notification_log (
    NotificationLogKey     BIGINT IDENTITY(1,1) PRIMARY KEY,
    NotificationTypeKey      INT              NOT NULL REFERENCES web.dim_notification_type(NotificationTypeKey),
    SentDate                    DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    Detail                        NVARCHAR(2000)   NULL
);
GO


/* ============================================================================
   14. RISK SCORING -- Design_Risk-Scoring.md (D-101-105, D-119-D-124)
   ============================================================================ */

-- web.dim_target_identifier_type: the ways a Target can be identified -- a
-- new kind is a seeded row. MatchPriority: lower is checked first during
-- import matching; RequiresReview = 1 sends a match on this type alone to
-- analyst review instead of auto-merging (D-102: IP-only matches).
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
GO

-- web.dim_target_type (D-124): what kind of destination a Target is -- a
-- stable code plus a DisplayName for the UI ('LdapDirectory' shows as
-- 'LDAP Directory'). A new type is a seeded row.
CREATE TABLE web.dim_target_type (
    TargetTypeKey  INT IDENTITY(1,1) PRIMARY KEY,
    TypeCode       NVARCHAR(50)  NOT NULL UNIQUE,  -- stable code, e.g. 'LdapDirectory' -- never shown to a user directly
    DisplayName    NVARCHAR(100) NOT NULL          -- e.g. 'LDAP Directory' -- what the UI actually renders
);

INSERT INTO web.dim_target_type (TypeCode, DisplayName) VALUES
    ('Server', 'Server'),
    ('Desktop', 'Desktop'),
    ('Database', 'Database'),
    ('Application', 'Application'),
    ('LdapDirectory', 'LDAP Directory'),
    ('ActiveDirectory', 'Active Directory'),
    ('Appliance', 'Appliance'),
    ('Other', 'Other');
GO

-- web.dim_target: any final destination an account's access leads to.
-- RiskScore is analyst-set (0-1000); the one exception is the placeholder 0
-- on Targets the nightly load creates from account addresses (D-123).
CREATE TABLE web.dim_target (
    TargetKey            INT IDENTITY(1,1) PRIMARY KEY,
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
    TargetTypeKey         INT              NOT NULL REFERENCES web.dim_target_type(TargetTypeKey),
    CONSTRAINT UQ_dim_target_InternalGuid UNIQUE (InternalGuid),
    CONSTRAINT CK_dim_target_RiskScore CHECK (RiskScore BETWEEN 0 AND 1000)
);
GO

-- web.target_identifier: every identifier value a Target is known by.
-- UNIQUE(IdentifierType, IdentifierValue): one real identifier value points
-- at one Target only.
CREATE TABLE web.target_identifier (
    TargetIdentifierKey  INT IDENTITY(1,1) PRIMARY KEY,
    TargetKey               INT NOT NULL REFERENCES web.dim_target(TargetKey),
    IdentifierType             NVARCHAR(50) NOT NULL REFERENCES web.dim_target_identifier_type(IdentifierType),
    IdentifierValue               NVARCHAR(300) NOT NULL,
    CONSTRAINT UQ_target_identifier UNIQUE (IdentifierType, IdentifierValue)
);
GO

-- web.dim_sor_type (D-121): where an Access Group's Source of Record lives.
CREATE TABLE web.dim_sor_type (
    SorTypeKey   INT IDENTITY(1,1) PRIMARY KEY,
    SorTypeName  NVARCHAR(50) NOT NULL UNIQUE
);

INSERT INTO web.dim_sor_type (SorTypeName) VALUES
    ('Domain'),
    ('Local'),
    ('App');
GO

-- web.dim_access_group: a privileged-access group in the managed environment
-- (e.g. an AD "Server Admins" group) -- not dbo.dim_group (CyberArk vault
-- groups) and not web.identity_group_role_map (BlueTrack's own sign-in groups).
-- D-122: unique on (GroupName, GroupIdentifier, FoundOnTargetKey), so a
-- well-known local group (the same SID on every server) can be recorded once
-- per server. FoundOnTargetKey is NULL for Domain groups, and a UNIQUE
-- constraint treats NULLs as distinct, so the persisted
-- FoundOnTargetKeyForUniqueness (NULL becomes -1) carries the constraint;
-- AccessGroupRepository.EnsureNoDuplicateAsync checks the same in the app.
-- InternalGuid mirrors web.dim_target's: a stable identity for the row.
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
    SorTypeKey            INT              NULL REFERENCES web.dim_sor_type(SorTypeKey),   -- D-121
    SorAddress            NVARCHAR(300)    NULL,                                           -- D-121, e.g. 'company.com'
    InternalGuid          UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
    FoundOnTargetKeyForUniqueness AS (ISNULL(FoundOnTargetKey, -1)) PERSISTED,
    CONSTRAINT CK_dim_access_group_BaseRiskScore CHECK (BaseRiskScore BETWEEN 0 AND 1000),
    CONSTRAINT CK_dim_access_group_ComputedRiskScore CHECK (ComputedRiskScore IS NULL OR ComputedRiskScore BETWEEN 0 AND 1000),
    CONSTRAINT UQ_dim_access_group_InternalGuid UNIQUE (InternalGuid),
    CONSTRAINT UQ_dim_access_group_NameIdentifierFoundOn UNIQUE (GroupName, GroupIdentifier, FoundOnTargetKeyForUniqueness)
);
GO

-- web.access_group_target_map: which targets a group grants access to.
CREATE TABLE web.access_group_target_map (
    AccessGroupKey        INT NOT NULL REFERENCES web.dim_access_group(AccessGroupKey),
    TargetKey                INT NOT NULL REFERENCES web.dim_target(TargetKey),
    ImportBatchId               UNIQUEIDENTIFIER NULL,
    SourceFileName                 NVARCHAR(260)    NULL,
    LoadTimestamp                     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (AccessGroupKey, TargetKey)
);
GO

-- web.account_access_group_map: which accounts belong to which access groups.
CREATE TABLE web.account_access_group_map (
    AccountKey            BIGINT NOT NULL REFERENCES dbo.fact_account(AccountKey),
    AccessGroupKey           INT    NOT NULL REFERENCES web.dim_access_group(AccessGroupKey),
    ImportBatchId               UNIQUEIDENTIFIER NULL,
    SourceFileName                 NVARCHAR(260)    NULL,
    LoadTimestamp                     DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (AccountKey, AccessGroupKey)
);
GO

-- web.account_target_map: DIRECT account-to-target access, bypassing groups.
-- SourceMethod: PendingSafeDerived (Phase C), ManualImport / ETL (CSV
-- imports, Phase B), AddressDerived (the account's Address names the Target,
-- D-193 -- kept in step by every nightly load).
CREATE TABLE web.account_target_map (
    AccountKey            BIGINT NOT NULL REFERENCES dbo.fact_account(AccountKey),
    TargetKey                INT    NOT NULL REFERENCES web.dim_target(TargetKey),
    SourceMethod                NVARCHAR(20)     NOT NULL,
    ImportBatchId                  UNIQUEIDENTIFIER NULL,
    SourceFileName                    NVARCHAR(260)    NULL,
    LoadTimestamp                        DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    PRIMARY KEY (AccountKey, TargetKey),
    CONSTRAINT CK_account_target_map_SourceMethod CHECK (SourceMethod IN ('PendingSafeDerived', 'ManualImport', 'ETL', 'AddressDerived'))
);
GO

-- web.account_risk_score: the computed account score, plus an analyst
-- override that wins when set. Its own table (not a fact_account_progress
-- column) because it has its own staleness/recalculation lifecycle.
-- Clearing OverrideRiskScore reverts to ComputedRiskScore by construction.
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
GO

-- web.target_match_review: a weak (IP-only, etc.) match an analyst confirms
-- rather than one the import merges or splits silently.
CREATE TABLE web.target_match_review (
    TargetMatchReviewKey  INT IDENTITY(1,1) PRIMARY KEY,
    CandidateTargetKey       INT              NULL REFERENCES web.dim_target(TargetKey),
    IdentifierType              NVARCHAR(50)     NOT NULL REFERENCES web.dim_target_identifier_type(IdentifierType),
    IdentifierValue                NVARCHAR(300)    NOT NULL,
    ImportBatchId                     UNIQUEIDENTIFIER NULL,
    SourceFileName                       NVARCHAR(260)    NULL,
    CreatedDate                             DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    ResolvedBy                                 INT              NULL REFERENCES web.app_user(UserKey),
    ResolvedDate                                  DATETIME2        NULL,
    Resolution                                       NVARCHAR(20)     NULL,
    CONSTRAINT CK_target_match_review_Resolution CHECK (Resolution IS NULL OR Resolution IN ('Merged', 'NewTarget', 'Ignored'))
);
GO

-- web.import_mapping_profile / import_mapping_field (D-105): one named
-- mapping per source file shape (several per FeedType allowed), and which
-- source column feeds which internal field.
CREATE TABLE web.import_mapping_profile (
    ImportMappingProfileKey INT IDENTITY(1,1) PRIMARY KEY,
    FeedType                   NVARCHAR(50)     NOT NULL,
    ProfileName                   NVARCHAR(200)    NOT NULL,
    IsActive                         BIT              NOT NULL DEFAULT 1,
    Description                         NVARCHAR(1000)   NULL,
    CreatedBy                             INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedBy                               INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedDate                                DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_import_mapping_profile UNIQUE (FeedType, ProfileName),
    CONSTRAINT CK_import_mapping_profile_FeedType CHECK (FeedType IN ('TargetInventory', 'AccessGroupInventory', 'AccessGroupTargetMap', 'AccountAccessGroupMembership', 'AccountTargetMap'))
);
GO

CREATE TABLE web.import_mapping_field (
    ImportMappingFieldKey   INT IDENTITY(1,1) PRIMARY KEY,
    ImportMappingProfileKey    INT              NOT NULL REFERENCES web.import_mapping_profile(ImportMappingProfileKey),
    SourceColumnName              NVARCHAR(200)    NOT NULL,
    TargetFieldName                  NVARCHAR(100)    NOT NULL,
    IsRequired                          BIT              NOT NULL DEFAULT 0,
    DefaultValue                           NVARCHAR(300)    NULL,
    CONSTRAINT UQ_import_mapping_field UNIQUE (ImportMappingProfileKey, TargetFieldName)
);
GO

-- web.dim_risk_score_band (D-120): named, ordered ranges over
-- EffectiveRiskScore -- not dbo.dim_risk_level (a manual label with no
-- numeric score, a different concept). RiskOrder is what sorts key off.
CREATE TABLE web.dim_risk_score_band (
    RiskScoreBandKey  INT IDENTITY(1,1) PRIMARY KEY,
    BandName          NVARCHAR(50)  NOT NULL,
    MinScore          INT NOT NULL,
    MaxScore          INT NOT NULL,
    RiskOrder         INT NOT NULL,
    ModifiedBy        INT NULL REFERENCES web.app_user(UserKey),
    ModifiedDate      DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT CK_dim_risk_score_band_range CHECK (MinScore <= MaxScore)
);

INSERT INTO web.dim_risk_score_band (BandName, MinScore, MaxScore, RiskOrder) VALUES
    ('Low', 0, 250, 10),
    ('Medium', 251, 500, 20),
    ('High', 501, 750, 30),
    ('Critical', 751, 1000, 40);
GO

-- web.AccessGroupKeyList: a set of Access Group keys, passed to the
-- "score this set of groups" functions (usp_CalculateRiskScoreForAccessGroupSet).
CREATE TYPE web.AccessGroupKeyList AS TABLE (AccessGroupKey INT NOT NULL PRIMARY KEY);
GO


/* ============================================================================
   15. AD ACCOUNT DISCOVERY -- real AD accounts not yet onboarded into
       CyberArk, found through the Access Group inventory (2026-09-16)
   ============================================================================ */

-- web.discovered_account: one row per candidate. UNIQUE (DomainName,
-- SamAccountName) is the upsert key. PossibleExistingAccountKey is a
-- best-effort hint for the reviewer, never used to include/exclude.
-- Status 'New' / 'Accepted' / 'Dismissed'; Accept creates the fact_account
-- row ResolvedAccountKey points at.
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
    Status                 NVARCHAR(20)     NOT NULL CONSTRAINT DF_discovered_account_Status DEFAULT 'New',
    ResolvedAccountKey     BIGINT           NULL REFERENCES dbo.fact_account(AccountKey),
    ReviewedBy             INT              NULL REFERENCES web.app_user(UserKey),
    ReviewedDate           DATETIME2        NULL,
    CONSTRAINT UQ_discovered_account_Domain_SamAccountName UNIQUE (DomainName, SamAccountName)
);
GO

-- Which inventoried Access Groups a candidate's AD membership matched.
CREATE TABLE web.discovered_account_access_group_map (
    DiscoveredAccountKey INT NOT NULL REFERENCES web.discovered_account(DiscoveredAccountKey),
    AccessGroupKey          INT NOT NULL REFERENCES web.dim_access_group(AccessGroupKey),
    PRIMARY KEY (DiscoveredAccountKey, AccessGroupKey)
);
GO


/* ============================================================================
   16. DATA FEEDS -- scheduled CSV imports (Planning_Data-Sources.md, D-181)
   ============================================================================ */

-- web.data_feed: one row per feed -- which import it runs, where its file is,
-- an optional mapping profile.
CREATE TABLE web.data_feed (
    DataFeedKey            INT IDENTITY(1,1) PRIMARY KEY,
    DisplayName              NVARCHAR(200)    NOT NULL,
    FeedType                   NVARCHAR(50)     NOT NULL,
    FolderPath                   NVARCHAR(500)    NOT NULL,
    FileNamePattern                 NVARCHAR(260)    NOT NULL,   -- may contain {yyyy-MM-dd} and * ; newest match wins
    ImportMappingProfileKey            INT              NULL REFERENCES web.import_mapping_profile(ImportMappingProfileKey),
    IsEnabled                             BIT              NOT NULL DEFAULT 1,
    DisplayOrder                            INT              NOT NULL DEFAULT 0,
    CreatedBy                                 INT              NULL REFERENCES web.app_user(UserKey),
    CreatedDate                                 DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    ModifiedBy                                    INT              NULL REFERENCES web.app_user(UserKey),
    ModifiedDate                                    DATETIME2        NULL,
    CONSTRAINT UQ_data_feed_DisplayName UNIQUE (DisplayName),
    CONSTRAINT CK_data_feed_FeedType CHECK (FeedType IN (
        'TargetInventory', 'AccessGroupInventory', 'AccessGroupTargetMap',
        'AccountAccessGroupMembership', 'AccountTargetMap', 'Applications', 'SafeAssignments'))
);
GO

-- web.data_feed_run: one row per feed per run -- outcome, counts, row errors.
CREATE TABLE web.data_feed_run (
    DataFeedRunKey         BIGINT IDENTITY(1,1) PRIMARY KEY,
    DataFeedKey              INT              NOT NULL REFERENCES web.data_feed(DataFeedKey) ON DELETE CASCADE,
    TriggerType                NVARCHAR(20)     NOT NULL,            -- 'Schedule' / 'RunNow'
    TriggeredBy                  INT              NULL REFERENCES web.app_user(UserKey),
    StartedAt                      DATETIME2        NOT NULL,
    FinishedAt                       DATETIME2        NULL,
    Outcome                            NVARCHAR(30)     NOT NULL,       -- 'Succeeded' / 'CompletedWithErrors' / 'Failed'
    FileName                             NVARCHAR(500)    NULL,
    TotalRows                              INT              NULL,
    ErrorRows                                INT              NULL,
    Summary                                    NVARCHAR(500)    NULL,  -- one-line human summary of the counts
    ResultJson                                   NVARCHAR(MAX)    NULL,  -- the import's own result, row errors included
    ErrorMessage                                   NVARCHAR(2000)   NULL,  -- why a Failed run failed (no file, unreadable, ...)
    CONSTRAINT CK_data_feed_run_TriggerType CHECK (TriggerType IN ('Schedule', 'RunNow')),
    CONSTRAINT CK_data_feed_run_Outcome CHECK (Outcome IN ('Succeeded', 'CompletedWithErrors', 'Failed'))
);

CREATE INDEX IX_data_feed_run_Feed_Started ON web.data_feed_run (DataFeedKey, StartedAt DESC);
GO


/* ============================================================================
   17. ACCOUNT DELETION IN BLUETRACK -- always with a reason (D-185)
   ============================================================================ */

-- web.account_deletion: one row per account deleted in BlueTrack (who, when,
-- why). Undelete removes it.
CREATE TABLE web.account_deletion (
    AccountKey     BIGINT          NOT NULL PRIMARY KEY REFERENCES dbo.fact_account(AccountKey),
    DeletedBy      INT             NOT NULL REFERENCES web.app_user(UserKey),
    DeletedAt      DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    Reason         NVARCHAR(1000)  NOT NULL
);
GO

-- web.account_deletion_history: every delete and undelete, with its reason
-- (append-only).
CREATE TABLE web.account_deletion_history (
    HistoryKey     BIGINT IDENTITY(1,1) PRIMARY KEY,
    AccountKey     BIGINT          NOT NULL REFERENCES dbo.fact_account(AccountKey),
    Action         NVARCHAR(10)    NOT NULL CONSTRAINT CK_account_deletion_history_Action CHECK (Action IN ('Delete', 'Undelete')),
    Reason         NVARCHAR(1000)  NOT NULL,
    PerformedBy    INT             NOT NULL REFERENCES web.app_user(UserKey),
    PerformedAt    DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
    BatchId        NVARCHAR(20)    NULL      -- shared by the accounts of one bulk delete/undelete
);

CREATE INDEX IX_account_deletion_history_Account ON web.account_deletion_history (AccountKey, PerformedAt DESC);
GO


PRINT '04_BlueTrack_Baseline_WebSchema.sql complete.';
GO
