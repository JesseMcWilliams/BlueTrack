/* ============================================================================
   01_BlueTrack_CreateDatabase_Schema.sql

   RUN THIS FILE FIRST.

   Blueprint Progress Tracking Database -- Database & Table Creation
   Target: SQL Server / Azure SQL

   What this file does, in order:
     1. Switches context into the target database (USE $DatabaseName$) --
        the database itself must already exist by this point (see below)
     2. Creates every table the project needs -- staging (per source export),
        reference/dimension, fact, and tracking tables -- each guarded with
        an existence check + DROP before CREATE, so this file can be re-run
        from the top at any time
     3. Loads seed/reference data for the fixed-vocabulary tables (source
        systems, the vault's own text-code decode table, permissions,
        permission aliases, Blueprint stages, and progress statuses)

   DATABASE CREATION: this file used to open with DROP DATABASE BlueTrack /
   CREATE DATABASE BlueTrack before switching context. That's gone (fixed
   2026-09-03, following a real incident where a hardcoded database name
   here caused this file to drop and recreate the wrong database). SQL
   Server can't drop a database a connection is currently using, so
   dropping and creating the database can't safely live in the same
   connection/script as everything else here, which now always runs against
   the target database directly (for a correctly-scoped DbUp journal -- see
   App/Migrator/Program.cs's own comment for the full reasoning). Ensuring
   the target database exists (create-if-missing, never drop) is now
   App/Migrator's job, done once against master before this script ever
   runs. Something that wants a genuinely fresh database -- CI's disposable
   BlueTrackTest, per Design_Testing_Strategy.md -- drops it explicitly,
   as its own visible step, before invoking App/Migrator.

   $DatabaseName$ below is DbUp's substitution token (App/Migrator passes
   it from <connectionString>'s own Database/Initial Catalog) -- if you're
   running this file by hand instead of through App/Migrator, replace
   $DatabaseName$ with your actual target database name first.

   This file intentionally covers everything that would normally come from
   CyberArk's own EVD CreateDB.sql script -- the Self-Hosted staging tables
   below (stg_sh_*) mirror that script's table shapes so exported/copied
   data can be loaded here. This is NOT the live EVD replication database
   itself, and does not stand one up -- keep this project database and the
   actual production EVD target database separate. These stg_sh_* tables
   only ever receive data that's been exported or copied out of that real
   EVD database; they are not a substitute for it.

   *** BATCHING NOTE ***
   CREATE DATABASE must be the only statement in its batch, so it's
   followed by GO. USE is likewise given its own batch. Everything from
   that point on is plain CREATE TABLE / INSERT, which do not need to be
   isolated in their own batch, so the rest of this file runs as normal
   sequential statements.

   *** ORDERING NOTE (important if you ever run sections out of order) ***
   Tables below are created in dependency order (a table's foreign-key
   parents are always created first). The per-table DROP-then-CREATE guard
   is safe here specifically because this file is only ever run against a
   freshly-created, empty target database -- App/Migrator ensures the
   database exists (create-if-missing) before this script runs, and never
   drops it; a caller that wants a genuinely empty database (CI's disposable
   BlueTrackTest) drops it explicitly beforehand, as its own visible step.
   If you ever run only part of this file against an already-populated
   database, dropping a parent table out of order will fail with a
   foreign-key error from whichever child table still references it; you'd
   need to drop child tables first, in the reverse of the order below.
   ============================================================================ */

USE $DatabaseName$;
GO


/* ============================================================================
   1. REFERENCE TABLES WITH NO DEPENDENCIES
   ============================================================================ */

IF OBJECT_ID('dbo.dim_source_system', 'U') IS NOT NULL DROP TABLE dbo.dim_source_system;
CREATE TABLE dim_source_system (
    SourceSystemKey     INT IDENTITY(1,1) PRIMARY KEY,
    SourceSystemCode    NVARCHAR(50)   NOT NULL UNIQUE,   -- 'PRIVCLOUD', 'SELFHOSTED', 'DISCOVERY'
    SourceSystemName    NVARCHAR(200)  NOT NULL
);

INSERT INTO dim_source_system (SourceSystemCode, SourceSystemName) VALUES
    ('PRIVCLOUD',  'CyberArk Privilege Cloud (SaaS)'),
    ('SELFHOSTED', 'CyberArk Self-Hosted Vault'),
    ('DISCOVERY',  'Discovered Accounts');


-- The vault's own reference/decode table, loaded verbatim from the seed
-- INSERTs shipped in CyberArk_EVD_Self-Hosted_CreateDB.sql (CATextCodes).
-- Used to decode the coded INT columns in the stg_sh_* tables below (e.g.
-- CodeType 12: 1=File, 2=Password; CodeType 10: 0=User, 1=Group, 2=Gateway
-- account). This is copied verbatim from the source script you provided,
-- not inferred.
IF OBJECT_ID('dbo.dim_selfhosted_code', 'U') IS NOT NULL DROP TABLE dbo.dim_selfhosted_code;
CREATE TABLE dim_selfhosted_code (
    CodeType            INT NOT NULL,
    CodeValue           INT NOT NULL,
    CodeText            NVARCHAR(256) NULL,
    PRIMARY KEY (CodeType, CodeValue)
);

INSERT INTO dim_selfhosted_code (CodeType, CodeValue, CodeText) VALUES
    (1, 1, N'Password'),
    (1, 2, N'PKI'),
    (1, 4, N'SECUREID'),
    (1, 8, N'NTAuth'),
    (1, 16, N'RADIUS'),
    (2, 0, N'None'),
    (2, 1, N'Users Administrators'),
    (2, 2, N'Safes Administrators'),
    (2, 4, N'Network Area Administrators'),
    (2, 8, N'User Templates Administrators'),
    (2, 16, N'File Categories Administrators'),
    (2, 32, N'Autdit All'),
    (2, 64, N'Backup All'),
    (2, 128, N'Restore All'),
    (3, 0, N'None'),
    (3, 1, N'Full'),
    (3, 2, N'Partial'),
    (3, 4, N'LogonAs'),
    (4, 1, N'Internal'),
    (4, 2, N'External'),
    (5, 1, N'Internal'),
    (5, 2, N'External'),
    (5, 4, N'Public (Internet)'),
    (6, 8, N'Unsecured'),
    (6, 16, N'Secure'),
    (6, 32, N'Highly Secured'),
    (7, 0, N'None'),
    (7, 1, N'Require Full Impersonation'),
    (7, 2, N'Require Partial Impersonation'),
    (7, 4, N'Require LogonAs Impersonation'),
    (7, 8, N'Require Authentication And Open'),
    (8, 0, N'None'),
    (8, 1, N'Open Safe'),
    (8, 2, N'Get File'),
    (8, 3, N'Open And Get'),
    (9, 0, N'None'),
    (9, 1, N'Accessed'),
    (9, 2, N'New'),
    (9, 4, N'Modified'),
    (9, 7, N'All'),
    (10, 0, N'User'),
    (10, 1, N'Group'),
    (10, 2, N'Gateway account'),
    (11, 1, N'Pending'),
    (11, 2, N'Valid'),
    (11, 4, N'Invalid'),
    (12, 1, N'File'),
    (12, 2, N'Password'),
    (13, 2, N'User log record'),
    (13, 3, N'Safe log record'),
    (14, 0, N'None'),
    (14, 1, N'User'),
    (14, 2, N'Location'),
    (14, 3, N'File/Password'),
    (14, 4, N'Network area'),
    (14, 5, N'Category'),
    (15, 1, N'Open Safe'),
    (15, 2, N'Get File'),
    (15, 4, N'Get Password'),
    (16, 0, N'One time access'),
    (16, 1, N'Multiple access'),
    (17, 0, N'None'),
    (17, 1, N'Expired'),
    (17, 2, N'Already Used'),
    (17, 4, N'Damaged - Missing supervisor'),
    (17, 8, N'Damaged - Confirmation settings changes'),
    (17, 16, N'Damaged - Object deleted'),
    (17, 32, N'Damaged - Incompatible version'),
    (17, 64, N'ToDate passed'),
    (18, 1, N'Waiting'),
    (18, 2, N'Confirmed'),
    (19, 0, N'None'),
    (19, 1, N'Reject'),
    (19, 2, N'Confirm');


-- Account-type taxonomy is a curated business mapping, NOT something present
-- in any raw export. Created empty here -- populate via
-- platform_account_type_map after reviewing dim_platform once real data is
-- loaded. Do not infer categories automatically from PlatformID text.
IF OBJECT_ID('dbo.dim_account_type', 'U') IS NOT NULL DROP TABLE dbo.dim_account_type;
CREATE TABLE dim_account_type (
    AccountTypeKey       INT IDENTITY(1,1) PRIMARY KEY,
    AccountTypeName      NVARCHAR(100) NOT NULL UNIQUE  -- e.g. 'Domain Account', 'Local/OS Account', 'Cloud IAM',
                                                          -- 'Database Account', 'Network Device', 'Application/Service Account',
                                                          -- 'DevOps Secret', 'RPA Account', 'Infrastructure (PSM/CPM)', 'Emergency/Break-glass'
);


-- Source of Record (SOR): where the account actually lives/authenticates --
-- distinct from dim_source_system (which CyberArk deployment the *tracking
-- data* came from) and from dim_account_type (what kind of privileged
-- account it is). An account's SOR answers "what authoritative system
-- controls this identity" -- Active Directory, LDAP, a local OS account
-- store, a local database's own user store, a local application's own
-- user store, or something else. Seeded with a fixed starting list;
-- extend if you find a category that doesn't fit.
IF OBJECT_ID('dbo.dim_source_of_record', 'U') IS NOT NULL DROP TABLE dbo.dim_source_of_record;
CREATE TABLE dim_source_of_record (
    SORKey                INT IDENTITY(1,1) PRIMARY KEY,
    SORName                NVARCHAR(100) NOT NULL UNIQUE
);

INSERT INTO dim_source_of_record (SORName) VALUES
    ('Active Directory'),
    ('LDAP'),
    ('Local OS'),
    ('Local Database'),
    ('Local Application'),
    ('Other');


-- Normalized permission catalog shared by both PRIVCLOUD and SELFHOSTED
-- entitlement data (see permission_alias below for the per-source mapping).
IF OBJECT_ID('dbo.dim_permission', 'U') IS NOT NULL DROP TABLE dbo.dim_permission;
CREATE TABLE dim_permission (
    PermissionKey       INT IDENTITY(1,1) PRIMARY KEY,
    PermissionName      NVARCHAR(100) NOT NULL UNIQUE
);

INSERT INTO dim_permission (PermissionName) VALUES
    ('UseAccounts'), ('RetrieveAccounts'), ('ListAccounts'), ('AddAccounts'),
    ('UpdateAccountContent'), ('UpdateAccountProperties'),
    ('InitiateCPMAccountManagementOperations'), ('SpecifyNextAccountContent'),
    ('RenameAccounts'), ('DeleteAccounts'), ('UnlockAccounts'),
    ('ManageSafe'), ('ManageSafeMembers'), ('BackupSafe'), ('ViewAuditLog'),
    ('ViewSafeMembers'), ('AccessWithoutConfirmation'), ('CreateFolders'),
    ('DeleteFolders'), ('MoveAccountsAndFolders'),
    ('RequestsAuthorizationLevel1'), ('RequestsAuthorizationLevel2'),
    -- Added 2026-09-01: Self-Hosted's CAOwners exposes moving a safe object
    -- "from" and "into" as two distinct raw flags (CAOMoveFrom/CAOMoveInto);
    -- they used to both alias to the single MoveAccountsAndFolders entry
    -- above, which caused a duplicate-key error in
    -- usp_Load_FactSafeEntitlement whenever an owner had both granted (the
    -- common case). Privilege Cloud only ever reports the single combined
    -- MoveAccountsAndFolders flag -- it has no from/into split -- so that
    -- entry is left as-is rather than repurposed, and these two are
    -- Self-Hosted-only (not directly cross-source comparable to PC's
    -- combined flag; see the permission_alias mapping below).
    ('MoveAccountsFrom'), ('MoveAccountsInto');


-- Suggested seed values map to a five-stage working framework for this
-- project's tracking -- confirm the exact stage names/count against your
-- organization's actual Blueprint roadmap document before treating these
-- as authoritative (see Account_Lifecycle_Onboarding_Flow.docx).
IF OBJECT_ID('dbo.dim_blueprint_stage', 'U') IS NOT NULL DROP TABLE dbo.dim_blueprint_stage;
CREATE TABLE dim_blueprint_stage (
    StageKey             INT IDENTITY(1,1) PRIMARY KEY,
    StageOrder           INT NOT NULL,
    StageName             NVARCHAR(100) NOT NULL UNIQUE
);

INSERT INTO dim_blueprint_stage (StageOrder, StageName) VALUES
    (1, 'Discovered'),
    (2, 'Assessed / Prioritized'),
    (3, 'Onboarded to Vault'),
    (4, 'Managed / Rotation Enabled'),
    (5, 'Verified / Compliant');


IF OBJECT_ID('dbo.dim_progress_status', 'U') IS NOT NULL DROP TABLE dbo.dim_progress_status;
CREATE TABLE dim_progress_status (
    StatusKey            INT IDENTITY(1,1) PRIMARY KEY,
    StatusName            NVARCHAR(100) NOT NULL UNIQUE
);

INSERT INTO dim_progress_status (StatusName) VALUES
    ('Not Started'), ('In Progress'), ('Blocked'), ('Complete'), ('Risk Accepted / Excluded');


-- Risk level is assigned by an analyst during Stage 2 (Assessed /
-- Prioritized), same curation pattern as AccountTypeKey/SORKey on
-- fact_account_progress -- not derived automatically. RiskOrder gives a
-- stable low-to-high sort for reporting (e.g. Power BI axis ordering)
-- without relying on alphabetical order, which would put Critical before
-- High and after Low.
IF OBJECT_ID('dbo.dim_risk_level', 'U') IS NOT NULL DROP TABLE dbo.dim_risk_level;
CREATE TABLE dim_risk_level (
    RiskLevelKey          INT IDENTITY(1,1) PRIMARY KEY,
    RiskOrder              INT NOT NULL,
    RiskLevelName           NVARCHAR(50) NOT NULL UNIQUE
);

INSERT INTO dim_risk_level (RiskOrder, RiskLevelName) VALUES
    (1, 'Low'),
    (2, 'Medium'),
    (3, 'High'),
    (4, 'Critical');


-- Empty shell here; populated by a date-generation script in
-- 04_BlueTrack_PowerBI_Support.sql (not "source" data, so it's kept
-- with the reporting file rather than seeded here).
IF OBJECT_ID('dbo.dim_date', 'U') IS NOT NULL DROP TABLE dbo.dim_date;
CREATE TABLE dim_date (
    DateKey              INT PRIMARY KEY,        -- yyyymmdd
    FullDate             DATE NOT NULL,
    Year                 INT NOT NULL,
    Quarter              INT NOT NULL,
    Month                INT NOT NULL,
    MonthName             NVARCHAR(20) NOT NULL,
    Week                 INT NOT NULL,
    DayOfMonth           INT NOT NULL,
    DayName               NVARCHAR(20) NOT NULL
);


/* ============================================================================
   2. STAGING TABLES -- one per source export, no foreign keys, raw shape
   ============================================================================ */

-- --- Privilege Cloud (SaaS) -------------------------------------------------

IF OBJECT_ID('dbo.stg_pc_platforms', 'U') IS NOT NULL DROP TABLE dbo.stg_pc_platforms;
CREATE TABLE stg_pc_platforms (
    LoadId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    ImportBatchId        UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),

    PlatformID            NVARCHAR(100)    NOT NULL,
    Name                  NVARCHAR(200)    NULL,
    Description           NVARCHAR(1000)   NULL,
    Active                BIT              NULL,
    PlatformType          NVARCHAR(50)     NULL   -- observed values inconsistent case: 'regular' / 'Regular' / 'group'
);

IF OBJECT_ID('dbo.stg_pc_users', 'U') IS NOT NULL DROP TABLE dbo.stg_pc_users;
CREATE TABLE stg_pc_users (
    LoadId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    ImportBatchId        UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),

    UserID                NVARCHAR(50)     NOT NULL,
    Username              NVARCHAR(200)    NOT NULL,
    UserType              NVARCHAR(100)    NULL,
    Source                NVARCHAR(100)    NULL,
    ComponentUser         BIT              NULL,
    Email                 NVARCHAR(320)    NULL,
    FirstName             NVARCHAR(200)    NULL,
    LastName              NVARCHAR(200)    NULL
);

IF OBJECT_ID('dbo.stg_pc_groups', 'U') IS NOT NULL DROP TABLE dbo.stg_pc_groups;
CREATE TABLE stg_pc_groups (
    LoadId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    ImportBatchId        UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),

    GroupID               NVARCHAR(50)     NOT NULL,
    GroupName             NVARCHAR(200)    NOT NULL,
    Description           NVARCHAR(1000)   NULL,
    Location              NVARCHAR(500)    NULL,
    GroupType             NVARCHAR(50)     NULL,
    DirectoryType         NVARCHAR(100)    NULL
);

IF OBJECT_ID('dbo.stg_pc_safes', 'U') IS NOT NULL DROP TABLE dbo.stg_pc_safes;
CREATE TABLE stg_pc_safes (
    LoadId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    ImportBatchId        UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),

    SafeUrlId             NVARCHAR(300)    NOT NULL,
    SafeName              NVARCHAR(300)    NOT NULL,
    SafeNumber            INT              NOT NULL,
    Description           NVARCHAR(1000)   NULL,
    Location              NVARCHAR(500)    NULL,
    CreatorId             NVARCHAR(100)    NULL,
    Creator               NVARCHAR(300)    NULL,
    OLACEnabled           BIT              NULL,
    ManagingCPM           NVARCHAR(200)    NULL,
    VersionRetention      INT              NULL,
    DayRetention          INT              NULL,
    AutoPurge             BIT              NULL,
    Created               DATE             NULL,
    LastModified          DATE             NULL,
    IsExpiredMember       BIT              NULL
);

IF OBJECT_ID('dbo.stg_pc_accounts', 'U') IS NOT NULL DROP TABLE dbo.stg_pc_accounts;
CREATE TABLE stg_pc_accounts (
    LoadId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    ImportBatchId        UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),

    AccountID             NVARCHAR(50)     NOT NULL,   -- composite-looking id, e.g. '15_5' -- treat as string, not numeric
    AccountName           NVARCHAR(500)    NOT NULL,
    Address               NVARCHAR(300)    NULL,
    UserName              NVARCHAR(300)    NULL,
    PlatformID            NVARCHAR(100)    NULL,
    SafeName              NVARCHAR(300)    NOT NULL,
    SecretType            NVARCHAR(50)     NULL,
    AutoManaged           BIT              NULL,
    CPMStatus             NVARCHAR(50)     NULL,
    ManualReason          NVARCHAR(500)    NULL,
    LastCPMModified       DATE             NULL,
    LastReconciled        DATE             NULL,
    LastVerified          DATE             NULL,
    RemoteMachines        NVARCHAR(1000)   NULL,
    RemoteAccessRestricted BIT             NULL,
    CategoryModified      DATE             NULL,
    Deleted               BIT              NULL,
    Created               DATE             NULL,
    Platform_LogonDomain  NVARCHAR(200)    NULL
);

IF OBJECT_ID('dbo.stg_pc_entitlements', 'U') IS NOT NULL DROP TABLE dbo.stg_pc_entitlements;
CREATE TABLE stg_pc_entitlements (
    LoadId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    ImportBatchId        UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    ExportDate            DATE             NULL,

    SafeUrlId             NVARCHAR(300)    NOT NULL,
    SafeName              NVARCHAR(300)    NOT NULL,
    SafeNumber            INT              NOT NULL,
    MemberId              NVARCHAR(100)    NOT NULL,   -- mixed formats observed: plain int-as-string AND GUID -- do not cast to INT
    MemberName            NVARCHAR(300)    NOT NULL,
    MemberType            NVARCHAR(20)     NOT NULL,   -- 'User' or 'Group'
    MembershipExpirationDate DATE          NULL,
    IsExpiredMembershipEnable BIT          NULL,
    IsPredefinedUser      BIT              NULL,
    UseAccounts                              BIT NULL,
    RetrieveAccounts                         BIT NULL,
    ListAccounts                             BIT NULL,
    AddAccounts                              BIT NULL,
    UpdateAccountContent                     BIT NULL,
    UpdateAccountProperties                  BIT NULL,
    InitiateCPMAccountManagementOperations   BIT NULL,
    SpecifyNextAccountContent                BIT NULL,
    RenameAccounts                           BIT NULL,
    DeleteAccounts                           BIT NULL,
    UnlockAccounts                           BIT NULL,
    ManageSafe                               BIT NULL,
    ManageSafeMembers                        BIT NULL,
    BackupSafe                               BIT NULL,
    ViewAuditLog                             BIT NULL,
    ViewSafeMembers                          BIT NULL,
    AccessWithoutConfirmation                BIT NULL,
    CreateFolders                            BIT NULL,
    DeleteFolders                            BIT NULL,
    MoveAccountsAndFolders                   BIT NULL,
    RequestsAuthorizationLevel1              BIT NULL,
    RequestsAuthorizationLevel2              BIT NULL
);

-- Local group membership export -- maps each Privilege Cloud group to its
-- direct User members. Confirmed against the real export you provided:
-- MemberID lines up with stg_pc_users.UserID for most rows, but ~2% of rows
-- in a typical export are built-in system users (e.g. 'Administrator',
-- 'Backup', 'Auditor', 'Operator', 'DR', 'TelemetryUser') that don't appear
-- in the Users export at all -- those won't resolve to a dim_user row
-- downstream, by design (see usp_Load_GroupMembership in
-- 02_BlueTrack_ETL_LoadProcedures.sql). RootGroupName was confirmed
-- to match stg_pc_groups.GroupName exactly (100% overlap in the sample),
-- which is what the load join relies on rather than a GroupID (not present
-- in this export). Only 'Parent' was observed in MemberLevel and only
-- 'User' in MemberType in the sample provided -- if a future export
-- contains nested/child group memberships or Group-type members, the load
-- logic below only handles direct User members and would need extending.
IF OBJECT_ID('dbo.stg_pc_groupmembers', 'U') IS NOT NULL DROP TABLE dbo.stg_pc_groupmembers;
CREATE TABLE stg_pc_groupmembers (
    LoadId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    ImportBatchId        UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),

    RootGroupName         NVARCHAR(300)    NOT NULL,
    MemberName            NVARCHAR(300)    NOT NULL,
    MemberID              NVARCHAR(50)     NOT NULL,
    MemberType            NVARCHAR(20)     NULL,   -- only 'User' observed
    MemberLevel           NVARCHAR(20)     NULL,   -- only 'Parent' (direct) observed
    Relationship           NVARCHAR(300)   NULL    -- observed identical to RootGroupName in the sample provided
);

-- --- Self-Hosted Vault (EVD) -------------------------------------------------
-- Mirrors CyberArk_EVD_Self-Hosted_CreateDB.sql's table shapes. See the
-- header note at the top of this file: these are staging mirrors for
-- exported/copied data, not the live EVD database itself.
--
-- STRUCTURAL DIFFERENCES FROM PRIVILEGE CLOUD, worth remembering while
-- reading these:
--  - No Platforms table exists in this source at all.
--  - No flat Accounts table exists either -- an account/password object is
--    a row in stg_sh_files, and its actual attributes (Address, UserName,
--    etc.) live in stg_sh_objectproperties as name/value pairs (EAV, one
--    row per property per file), not as columns.

IF OBJECT_ID('dbo.stg_sh_users', 'U') IS NOT NULL DROP TABLE dbo.stg_sh_users;
CREATE TABLE stg_sh_users (
    LoadId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    ImportBatchId        UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),

    CAUUserID             BIGINT           NOT NULL,
    CAUUserName           NVARCHAR(128)    NULL,
    CAULocationID         BIGINT           NULL,
    CAULocationName       NVARCHAR(128)    NULL,
    CAUFirstName          NVARCHAR(30)     NULL,
    CAULastName           NVARCHAR(30)     NULL,
    CAUBusinessEmail      NVARCHAR(50)     NULL,
    CAUDisabled           NVARCHAR(5)      NULL,   -- 'Yes'/'No' style flag in source -- confirm exact literal values before casting to BIT
    CAUExpirationDate     DATETIME         NULL,
    CAUPasswordNeverExpires NVARCHAR(5)    NULL,
    CAUAuthenticationMethods INT           NULL,
    CAUAuthorizations     INT              NULL,   -- bitmask -- decode via dim_selfhosted_code type 2
    CAUGatewayAccountAuthorizations INT    NULL,
    CAUDistinguishedName  NVARCHAR(512)    NULL,
    CAUExternalInternal   INT              NULL,   -- decode via dim_selfhosted_code type 4 (1=Internal, 2=External)
    CAULDAPFullDN         NVARCHAR(1024)   NULL,
    CAULDAPDirectory      NVARCHAR(256)    NULL,
    CAUMapName            NVARCHAR(128)    NULL,
    CAUMapID              BIGINT           NULL,
    CAULastLogonDate      DATETIME         NULL,
    CAUPrevLogonDate      DATETIME         NULL,
    CAUUserTypeID         INT              NULL,
    CAURestrictedInterfaces NVARCHAR(1024) NULL,
    CAUApplicationMetadata NVARCHAR(4000)  NULL,
    CAUCreationDate       DATETIME         NULL,
    CAUVaultID            NVARCHAR(28)     NULL
);

IF OBJECT_ID('dbo.stg_sh_groups', 'U') IS NOT NULL DROP TABLE dbo.stg_sh_groups;
CREATE TABLE stg_sh_groups (
    LoadId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    ImportBatchId        UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),

    CAGGroupID            BIGINT           NOT NULL,
    CAGGroupName          NVARCHAR(128)    NULL,
    CAGLocationID         BIGINT           NULL,
    CAGLocationName       NVARCHAR(128)    NULL,
    CAGDescription        NVARCHAR(100)    NULL,
    CAGExternalGroupName  NVARCHAR(128)    NULL,
    CAGExternalInternal   INT              NULL,   -- decode via dim_selfhosted_code type 4
    CAGLDAPFullDN         NVARCHAR(1024)   NULL,
    CAGLDAPDirectory      NVARCHAR(256)    NULL,
    CAGMapName            NVARCHAR(128)    NULL,
    CAGMapID              BIGINT           NULL,
    CAGVaultID            NVARCHAR(28)     NULL
);

IF OBJECT_ID('dbo.stg_sh_groupmembers', 'U') IS NOT NULL DROP TABLE dbo.stg_sh_groupmembers;
CREATE TABLE stg_sh_groupmembers (
    LoadId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    ImportBatchId        UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),

    CAGMGroupID           BIGINT           NULL,
    CAGMUserID            BIGINT           NULL,
    CAGMMemberIsGroup     NVARCHAR(5)      NULL,
    CAGMVaultID           NVARCHAR(28)     NULL
);

IF OBJECT_ID('dbo.stg_sh_safes', 'U') IS NOT NULL DROP TABLE dbo.stg_sh_safes;
CREATE TABLE stg_sh_safes (
    LoadId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    ImportBatchId        UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),

    CASSafeID             BIGINT           NOT NULL,
    CASSafeName           NVARCHAR(28)     NULL,
    CASLocationID         BIGINT           NULL,
    CASLocationName       NVARCHAR(128)    NULL,
    CASSize               BIGINT           NULL,
    CASMaxSize            BIGINT           NULL,
    CASUsedSize           INT              NULL,
    CASLastUsed           DATETIME         NULL,
    CASSecurityLevel      INT              NULL,
    CASDailyVersions      INT              NULL,
    CASMonthlyVersions    INT              NULL,
    CASYearlyVersions     INT              NULL,
    CASLogRetentionPeriod INT              NULL,
    CASObjectsRetentionPeriod INT          NULL,
    CASRequestsRetentionPeriod INT         NULL,
    CASConfirmersCount    INT              NULL,
    CASConfirmType        INT              NULL,   -- decode via dim_selfhosted_code type 3 (Full/Partial/LogonAs)
    CASRequireReasonToRetrieve NVARCHAR(5) NULL,
    CASEnforceExclusivePasswords NVARCHAR(5) NULL,
    CASRequireContentValidation NVARCHAR(5) NULL,
    CASCreationDate       DATETIME         NULL,
    CASCreatedBy          NVARCHAR(513)    NULL,
    CASNumberOfPasswordVersions INT        NULL,
    CASVaultID            NVARCHAR(28)     NULL
);

IF OBJECT_ID('dbo.stg_sh_owners', 'U') IS NOT NULL DROP TABLE dbo.stg_sh_owners;
CREATE TABLE stg_sh_owners (
    LoadId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    ImportBatchId        UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),

    CAOSafeID             BIGINT           NULL,
    CAOSafeName           NVARCHAR(28)     NULL,
    CAOOwnerID            BIGINT           NULL,
    CAOOwnerName          NVARCHAR(128)    NULL,
    CAOOwnerType          INT              NULL,   -- decode via dim_selfhosted_code type 10: 0=User, 1=Group, 2=Gateway account
    CAOExpirationDate     DATETIME         NULL,
    CAOList               NVARCHAR(5)      NULL,
    CAORetrieve           NVARCHAR(5)      NULL,
    CAOCreateObject       NVARCHAR(5)      NULL,
    CAOUpdateObject       NVARCHAR(5)      NULL,
    CAOUpdateObjectProperties NVARCHAR(5)  NULL,
    CAORenameObject       NVARCHAR(5)      NULL,
    CAODelete             NVARCHAR(5)      NULL,
    CAOViewAudit          NVARCHAR(5)      NULL,   -- added 2026-08-27 (Q-27): present in CyberArk_EVD_Self-Hosted_CreateDB.sql, missing here until now
    CAOViewOwners         NVARCHAR(5)      NULL,   -- added 2026-08-27 (Q-27)
    CAOUsePassword        NVARCHAR(5)      NULL,   -- added 2026-08-27 (Q-27)
    CAOInitiateCPMChange  NVARCHAR(5)      NULL,
    CAOInitiateCPMChangeWithManualPassword NVARCHAR(5) NULL,
    CAOCreateFolder       NVARCHAR(5)      NULL,
    CAODeleteFolder       NVARCHAR(5)      NULL,
    CAOUnlockObject       NVARCHAR(5)      NULL,
    CAOMoveFrom           NVARCHAR(5)      NULL,
    CAOMoveInto           NVARCHAR(5)      NULL,
    CAOManageSafe         NVARCHAR(5)      NULL,
    CAOManageSafeOwners   NVARCHAR(5)      NULL,
    CAOValidateSafeContent NVARCHAR(5)     NULL,
    CAOBackup             NVARCHAR(5)      NULL,
    CAONoConfirmRequired  NVARCHAR(5)      NULL,
    CAOConfirm            NVARCHAR(5)      NULL,
    CAOEventsList         NVARCHAR(5)      NULL,
    CAOEventsAdd          NVARCHAR(5)      NULL,
    CAOVaultID            NVARCHAR(28)     NULL
);

IF OBJECT_ID('dbo.stg_sh_files', 'U') IS NOT NULL DROP TABLE dbo.stg_sh_files;
CREATE TABLE stg_sh_files (
    LoadId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    ImportBatchId        UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),

    CAFSafeID             BIGINT           NULL,
    CAFSafeName           NVARCHAR(28)     NULL,
    CAFFolder             NVARCHAR(170)    NULL,
    CAFFileID             BIGINT           NOT NULL,
    CAFFileName           NVARCHAR(170)    NULL,
    CAFInternalName       NVARCHAR(28)     NULL,
    CAFSize               BIGINT           NULL,
    CAFCreatedBy          NVARCHAR(128)    NULL,
    CAFCreationDate       DATETIME         NULL,
    CAFLastUsedBy         NVARCHAR(128)    NULL,
    CAFLastUsedDate       DATETIME         NULL,
    CAFModificationDate   DATETIME         NULL,
    CAFModifiedBy         NVARCHAR(128)    NULL,
    CAFDeletedBy          NVARCHAR(128)    NULL,
    CAFDeletionDate       DATETIME         NULL,
    CAFLockDate           DATETIME         NULL,   -- added 2026-08-27 (Q-27): present in CyberArk_EVD_Self-Hosted_CreateDB.sql, missing here until now
    CAFLockBy             NVARCHAR(128)    NULL,   -- added 2026-08-27 (Q-27)
    CAFLockByID           BIGINT           NULL,   -- added 2026-08-27 (Q-27)
    CAFAccessed           NVARCHAR(5)      NULL,   -- added 2026-08-27 (Q-27): whether the password object has been accessed
    CAFNew                NVARCHAR(5)      NULL,   -- added 2026-08-27 (Q-27)
    CAFRetrieved          NVARCHAR(5)      NULL,   -- added 2026-08-27 (Q-27)
    CAFModified           NVARCHAR(5)      NULL,   -- added 2026-08-27 (Q-27)
    CAFIsRequestNeeded    NVARCHAR(5)      NULL,   -- added 2026-08-27 (Q-27)
    CAFValidationStatus   INT              NULL,   -- decode via dim_selfhosted_code type 11: 1=Pending, 2=Valid, 4=Invalid
    CAFType               INT              NULL,   -- decode via dim_selfhosted_code type 12: 1=File, 2=Password -- FILTER ON =2 to isolate account/password objects
    CAFCompressedSize     BIGINT           NULL,
    CAFLastModifiedDate   DATETIME         NULL,
    CAFLastModifiedBy     NVARCHAR(513)    NULL,
    CAFLastUsedByHuman    NVARCHAR(513)    NULL,
    CAFLastUsedHumanDate  DATETIME         NULL,
    CAFLastUsedByComponent NVARCHAR(513)   NULL,
    CAFLastUsedComponentDate DATETIME      NULL,
    CAFVaultID            NVARCHAR(28)     NULL
);

-- EAV table: one row per (file, property name). See header note -- an
-- account's Address, UserName, PolicyID/Platform, etc. live here as rows,
-- not columns. Load as-is; pivoting happens downstream (see
-- 02_BlueTrack_ETL_LoadProcedures.sql).
IF OBJECT_ID('dbo.stg_sh_objectproperties', 'U') IS NOT NULL DROP TABLE dbo.stg_sh_objectproperties;
CREATE TABLE stg_sh_objectproperties (
    LoadId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    ImportBatchId        UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),

    CAOPObjectPropertyId    BIGINT         NULL,
    CAOPObjectPropertyName  NVARCHAR(29)   NULL,   -- CONFIRM actual distinct values against real data before building any pivot
    CAOPSafeId               BIGINT        NULL,
    CAOPFileId                INT          NULL,
    CAOPObjectPropertyValue NVARCHAR(4000) NULL,
    CAOPOptions              BIGINT        NULL
);

-- Dual-control workflow tables. Not required for basic progress tracking,
-- but useful for flagging accounts stuck behind a pending access request.
IF OBJECT_ID('dbo.stg_sh_requests', 'U') IS NOT NULL DROP TABLE dbo.stg_sh_requests;
CREATE TABLE stg_sh_requests (
    LoadId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    ImportBatchId        UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),

    CARRequestID          INT              NULL,
    CARUserID             BIGINT           NULL,
    CARUserName           NVARCHAR(128)    NULL,
    CARSafeID             BIGINT           NULL,
    CARSafeName           NVARCHAR(28)     NULL,
    CARFileID             BIGINT           NULL,
    CARFileName           NVARCHAR(170)    NULL,
    CARReason             NVARCHAR(200)    NULL,
    CARCreationDate       DATETIME         NULL,
    CARExpirationDate     DATETIME         NULL,
    CARStatus             INT              NULL    -- decode via dim_selfhosted_code type 17
);

IF OBJECT_ID('dbo.stg_sh_confirmations', 'U') IS NOT NULL DROP TABLE dbo.stg_sh_confirmations;
CREATE TABLE stg_sh_confirmations (
    LoadId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    ImportBatchId        UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),

    CACRequestID          INT              NULL,
    CACSafeID             BIGINT           NULL,
    CACSafeName           NVARCHAR(28)     NULL,
    CACUserID             BIGINT           NULL,
    CACAction             INT              NULL     -- decode via dim_selfhosted_code type 19: 1=Reject, 2=Confirm
);

-- --- Discovered Accounts -----------------------------------------------------
-- STUB: no sample export provided yet for this source. Revise columns once
-- one is available -- do not assume it matches either source above.
IF OBJECT_ID('dbo.stg_discovered_accounts', 'U') IS NOT NULL DROP TABLE dbo.stg_discovered_accounts;
CREATE TABLE stg_discovered_accounts (
    LoadId               BIGINT IDENTITY(1,1) PRIMARY KEY,
    ImportBatchId        UNIQUEIDENTIFIER NOT NULL,
    SourceFileName        NVARCHAR(260)    NOT NULL,
    LoadTimestamp         DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
    RawColumnsPending     NVARCHAR(MAX)    NULL
);


/* ============================================================================
   3. DIMENSION TABLES DEPENDENT ON dim_source_system
   ============================================================================ */

IF OBJECT_ID('dbo.dim_platform', 'U') IS NOT NULL DROP TABLE dbo.dim_platform;
CREATE TABLE dim_platform (
    PlatformKey          INT IDENTITY(1,1) PRIMARY KEY,
    SourceSystemKey       INT             NOT NULL REFERENCES dim_source_system(SourceSystemKey),
    PlatformID            NVARCHAR(100)   NOT NULL,
    PlatformName          NVARCHAR(200)   NULL,
    Description           NVARCHAR(1000)  NULL,
    IsActive              BIT             NULL,
    PlatformType          NVARCHAR(50)    NULL,
    CONSTRAINT UQ_dim_platform UNIQUE (SourceSystemKey, PlatformID)
);

-- Self-Hosted organizes users/groups/safes in a folder-like Location
-- hierarchy (e.g. '\Windows\Servers'); Privilege Cloud's exports don't
-- carry this, so LocationKey stays NULL for Privilege Cloud rows.
IF OBJECT_ID('dbo.dim_location', 'U') IS NOT NULL DROP TABLE dbo.dim_location;
CREATE TABLE dim_location (
    LocationKey          INT IDENTITY(1,1) PRIMARY KEY,
    SourceSystemKey       INT NOT NULL REFERENCES dim_source_system(SourceSystemKey),
    SourceLocationId      NVARCHAR(50) NOT NULL,
    LocationName          NVARCHAR(256) NOT NULL,
    CONSTRAINT UQ_dim_location UNIQUE (SourceSystemKey, SourceLocationId)
);

-- Maps each source's raw permission column/name to the canonical
-- dim_permission entry. CONFIDENCE NOTE: pairs marked 'best-guess' in the
-- seed data below are inferred from column-name similarity, not confirmed
-- against CyberArk's permission documentation -- verify before relying on
-- cross-source permission comparisons.
IF OBJECT_ID('dbo.permission_alias', 'U') IS NOT NULL DROP TABLE dbo.permission_alias;
CREATE TABLE permission_alias (
    SourceSystemKey       INT NOT NULL REFERENCES dim_source_system(SourceSystemKey),
    RawPermissionName      NVARCHAR(100) NOT NULL,
    PermissionKey          INT NOT NULL REFERENCES dim_permission(PermissionKey),
    Confidence             NVARCHAR(20) NOT NULL DEFAULT 'confirmed',
    PRIMARY KEY (SourceSystemKey, RawPermissionName)
);

-- Privilege Cloud column names map 1:1 to dim_permission names already.
INSERT INTO permission_alias (SourceSystemKey, RawPermissionName, PermissionKey, Confidence)
SELECT ss.SourceSystemKey, dp.PermissionName, dp.PermissionKey, 'confirmed'
FROM dim_permission dp
CROSS JOIN dim_source_system ss
WHERE ss.SourceSystemCode = 'PRIVCLOUD';

-- Self-Hosted CAOwners column mappings -- review before trusting.
INSERT INTO permission_alias (SourceSystemKey, RawPermissionName, PermissionKey, Confidence)
SELECT ss.SourceSystemKey, v.RawName, dp.PermissionKey, v.Confidence
FROM dim_source_system ss
CROSS JOIN (VALUES
    ('CAOList',                                    'ListAccounts',                           'confirmed'),
    ('CAORetrieve',                                 'RetrieveAccounts',                       'confirmed'),
    ('CAOCreateObject',                             'AddAccounts',                            'best-guess'),
    ('CAOUpdateObject',                             'UpdateAccountContent',                   'confirmed'),
    ('CAOUpdateObjectProperties',                   'UpdateAccountProperties',                'confirmed'),
    ('CAORenameObject',                             'RenameAccounts',                         'confirmed'),
    ('CAODelete',                                   'DeleteAccounts',                         'best-guess'),
    ('CAOInitiateCPMChange',                        'InitiateCPMAccountManagementOperations', 'best-guess'),
    ('CAOInitiateCPMChangeWithManualPassword',      'SpecifyNextAccountContent',               'best-guess'),
    ('CAOCreateFolder',                             'CreateFolders',                          'confirmed'),
    ('CAODeleteFolder',                             'DeleteFolders',                          'confirmed'),
    ('CAOUnlockObject',                             'UnlockAccounts',                         'confirmed'),
    -- Corrected 2026-09-01: previously both mapped to MoveAccountsAndFolders,
    -- which caused a duplicate-key error whenever an owner had both granted.
    -- Now each maps to its own dedicated permission -- see the dim_permission
    -- seed comment above.
    ('CAOMoveFrom',                                 'MoveAccountsFrom',                       'confirmed'),
    ('CAOMoveInto',                                 'MoveAccountsInto',                       'confirmed'),
    ('CAOManageSafe',                               'ManageSafe',                             'confirmed'),
    ('CAOManageSafeOwners',                         'ManageSafeMembers',                      'confirmed'),
    ('CAOBackup',                                   'BackupSafe',                             'confirmed'),
    ('CAONoConfirmRequired',                        'AccessWithoutConfirmation',               'confirmed'),
    ('CAOEventsList',                               'ViewAuditLog',                           'best-guess')
    -- CAOValidateSafeContent and CAOConfirm/CAOEventsAdd have no obvious
    -- Privilege Cloud Entitlements counterpart -- left unmapped rather than
    -- guessed. Add dim_permission entries for them if you need to track
    -- these self-hosted-only permissions.
) AS v(RawName, PermissionName, Confidence)
JOIN dim_permission dp ON dp.PermissionName = v.PermissionName
WHERE ss.SourceSystemCode = 'SELFHOSTED';


/* ============================================================================
   4. DIMENSION TABLES DEPENDENT ON dim_source_system + dim_location
   ============================================================================ */

IF OBJECT_ID('dbo.dim_safe', 'U') IS NOT NULL DROP TABLE dbo.dim_safe;
CREATE TABLE dim_safe (
    SafeKey              INT IDENTITY(1,1) PRIMARY KEY,
    SourceSystemKey       INT             NOT NULL REFERENCES dim_source_system(SourceSystemKey),
    SafeUrlId             NVARCHAR(300)   NOT NULL,
    SafeName              NVARCHAR(300)   NOT NULL,
    SafeNumber            INT             NULL,
    Description           NVARCHAR(1000)  NULL,
    Location              NVARCHAR(500)   NULL,
    CreatorUsername       NVARCHAR(300)   NULL,
    OLACEnabled           BIT             NULL,
    ManagingCPM           NVARCHAR(200)   NULL,
    VersionRetention      INT             NULL,
    DayRetention          INT             NULL,
    AutoPurge             BIT             NULL,
    CreatedDate           DATE            NULL,
    LastModifiedDate      DATE            NULL,
    LocationKey           INT             NULL REFERENCES dim_location(LocationKey),
    CONSTRAINT UQ_dim_safe UNIQUE (SourceSystemKey, SafeUrlId)
);

IF OBJECT_ID('dbo.dim_user', 'U') IS NOT NULL DROP TABLE dbo.dim_user;
CREATE TABLE dim_user (
    UserKey              INT IDENTITY(1,1) PRIMARY KEY,
    SourceSystemKey       INT             NOT NULL REFERENCES dim_source_system(SourceSystemKey),
    SourceUserId          NVARCHAR(100)   NOT NULL,
    Username              NVARCHAR(300)   NOT NULL,
    UserType              NVARCHAR(100)   NULL,
    UserSource            NVARCHAR(100)   NULL,
    ComponentUser         BIT             NULL,
    Email                 NVARCHAR(320)   NULL,
    FirstName             NVARCHAR(200)   NULL,
    LastName              NVARCHAR(200)   NULL,
    LocationKey           INT             NULL REFERENCES dim_location(LocationKey),
    CONSTRAINT UQ_dim_user UNIQUE (SourceSystemKey, SourceUserId)
);

IF OBJECT_ID('dbo.dim_group', 'U') IS NOT NULL DROP TABLE dbo.dim_group;
CREATE TABLE dim_group (
    GroupKey             INT IDENTITY(1,1) PRIMARY KEY,
    SourceSystemKey       INT             NOT NULL REFERENCES dim_source_system(SourceSystemKey),
    SourceGroupId         NVARCHAR(100)   NOT NULL,
    GroupName             NVARCHAR(300)   NOT NULL,
    Description           NVARCHAR(1000)  NULL,
    Location              NVARCHAR(500)   NULL,
    GroupType             NVARCHAR(50)    NULL,
    DirectoryType         NVARCHAR(100)   NULL,
    LocationKey           INT             NULL REFERENCES dim_location(LocationKey),
    CONSTRAINT UQ_dim_group UNIQUE (SourceSystemKey, SourceGroupId)
);

-- Curated, manually-maintained mapping from a platform to an account-type
-- category AND a source-of-record. One review pass per Platform sets both
-- attributes at once, since a Platform typically implies both (e.g. a
-- "WinDomain" platform implies both Account Type = 'Domain Account' and
-- SOR = 'Active Directory'; an "Oracle" platform implies Account Type =
-- 'Database Account' and SOR = 'Local Database'). Populate after reviewing
-- dim_platform -- do not infer either column automatically from PlatformID
-- text.
IF OBJECT_ID('dbo.platform_account_type_map', 'U') IS NOT NULL DROP TABLE dbo.platform_account_type_map;
CREATE TABLE platform_account_type_map (
    PlatformKey          INT NOT NULL PRIMARY KEY REFERENCES dim_platform(PlatformKey),
    AccountTypeKey        INT NOT NULL REFERENCES dim_account_type(AccountTypeKey),
    SORKey                 INT NULL REFERENCES dim_source_of_record(SORKey),
    Notes                  NVARCHAR(500) NULL
);

IF OBJECT_ID('dbo.import_log', 'U') IS NOT NULL DROP TABLE dbo.import_log;
CREATE TABLE import_log (
    ImportBatchId         UNIQUEIDENTIFIER NOT NULL PRIMARY KEY DEFAULT NEWID(),
    SourceSystemKey        INT NOT NULL REFERENCES dim_source_system(SourceSystemKey),
    SourceFileName          NVARCHAR(260) NOT NULL,
    RowsLoaded              INT NULL,
    StartedAt                DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CompletedAt              DATETIME2 NULL,
    Status                   NVARCHAR(50) NOT NULL DEFAULT 'Started',
    ErrorMessage             NVARCHAR(2000) NULL
);


/* ============================================================================
   5. FACT TABLES -- unified account inventory + entitlements, cross-source
   ============================================================================ */

IF OBJECT_ID('dbo.fact_account', 'U') IS NOT NULL DROP TABLE dbo.fact_account;
CREATE TABLE fact_account (
    AccountKey            BIGINT IDENTITY(1,1) PRIMARY KEY,
    SourceSystemKey        INT             NOT NULL REFERENCES dim_source_system(SourceSystemKey),
    SourceAccountId         NVARCHAR(100)   NOT NULL,
    AccountName             NVARCHAR(500)   NOT NULL,
    Address                 NVARCHAR(300)   NULL,
    UserName                NVARCHAR(300)   NULL,
    PlatformKey              INT             NULL REFERENCES dim_platform(PlatformKey),
    SafeKey                  INT             NULL REFERENCES dim_safe(SafeKey),
    SecretType               NVARCHAR(50)    NULL,
    AutoManaged              BIT             NULL,
    CPMStatus                NVARCHAR(50)    NULL,
    ManualReason             NVARCHAR(500)   NULL,
    LastCPMModifiedDate      DATE            NULL,
    LastReconciledDate       DATE            NULL,
    LastVerifiedDate         DATE            NULL,
    RemoteAccessRestricted   BIT             NULL,
    IsDeleted                BIT             NOT NULL DEFAULT 0,
    CreatedDate              DATE            NULL,
    PlatformLogonDomain      NVARCHAR(200)   NULL,
    LastLoadBatchId          UNIQUEIDENTIFIER NULL,
    CONSTRAINT UQ_fact_account UNIQUE (SourceSystemKey, SourceAccountId)
);

IF OBJECT_ID('dbo.fact_safe_entitlement', 'U') IS NOT NULL DROP TABLE dbo.fact_safe_entitlement;
CREATE TABLE fact_safe_entitlement (
    EntitlementKey        BIGINT IDENTITY(1,1) PRIMARY KEY,
    SafeKey                INT             NOT NULL REFERENCES dim_safe(SafeKey),
    MemberType              NVARCHAR(20)    NOT NULL,
    UserKey                  INT             NULL REFERENCES dim_user(UserKey),
    GroupKey                 INT             NULL REFERENCES dim_group(GroupKey),
    MembershipExpirationDate DATE           NULL,
    IsExpiredMembershipEnable BIT           NULL,
    IsPredefinedUser          BIT           NULL,
    SnapshotDate              DATE          NOT NULL,
    LastLoadBatchId           UNIQUEIDENTIFIER NULL,
    CONSTRAINT CK_entitlement_member CHECK (
        (MemberType = 'User' AND UserKey IS NOT NULL AND GroupKey IS NULL) OR
        (MemberType = 'Group' AND GroupKey IS NOT NULL AND UserKey IS NULL)
    )
);

-- Normalized permission grants (long format) instead of 24+ boolean
-- columns -- makes "which safes grant X" or "who can retrieve passwords"
-- trivial to slice in Power BI without dozens of separate measures.
IF OBJECT_ID('dbo.bridge_entitlement_permission', 'U') IS NOT NULL DROP TABLE dbo.bridge_entitlement_permission;
CREATE TABLE bridge_entitlement_permission (
    EntitlementKey        BIGINT NOT NULL REFERENCES fact_safe_entitlement(EntitlementKey),
    PermissionKey          INT    NOT NULL REFERENCES dim_permission(PermissionKey),
    IsGranted               BIT    NOT NULL,
    PRIMARY KEY (EntitlementKey, PermissionKey)
);

-- Expands a Group's membership into individual Users -- lets a Group-type
-- safe entitlement in fact_safe_entitlement be resolved down to actual
-- people (see vw_effective_safe_access below). Privilege Cloud only for
-- now; Self-Hosted's stg_sh_groupmembers is imported but not yet loaded
-- into this bridge -- extend usp_Load_GroupMembership with an equivalent
-- Self-Hosted block if that's needed later (same shape, joining
-- CAGMGroupID/CAGMUserID to dim_group/dim_user by their Self-Hosted source
-- IDs instead of by name). Treated as a full-snapshot table like
-- fact_safe_entitlement -- current state, not an accumulating history.
IF OBJECT_ID('dbo.bridge_group_membership', 'U') IS NOT NULL DROP TABLE dbo.bridge_group_membership;
CREATE TABLE bridge_group_membership (
    GroupMembershipKey    BIGINT IDENTITY(1,1) PRIMARY KEY,
    GroupKey               INT  NOT NULL REFERENCES dim_group(GroupKey),
    UserKey                 INT  NOT NULL REFERENCES dim_user(UserKey),
    MemberLevel              NVARCHAR(20) NULL,
    SnapshotDate              DATE NOT NULL,
    CONSTRAINT UQ_bridge_group_membership UNIQUE (GroupKey, UserKey)
);


/* ============================================================================
   6. BLUEPRINT PROGRESS TRACKING + RECONCILIATION -- the core deliverable
   ============================================================================ */

IF OBJECT_ID('dbo.fact_account_progress', 'U') IS NOT NULL DROP TABLE dbo.fact_account_progress;
CREATE TABLE fact_account_progress (
    ProgressKey            BIGINT IDENTITY(1,1) PRIMARY KEY,
    AccountKey              BIGINT NOT NULL REFERENCES fact_account(AccountKey),
    CurrentStageKey          INT    NOT NULL REFERENCES dim_blueprint_stage(StageKey),
    CurrentStatusKey         INT    NOT NULL REFERENCES dim_progress_status(StatusKey),
    AccountTypeKey           INT    NULL REFERENCES dim_account_type(AccountTypeKey),
    SORKey                    INT    NULL REFERENCES dim_source_of_record(SORKey),
    RiskLevelKey              INT    NULL REFERENCES dim_risk_level(RiskLevelKey),
    OwnerName                NVARCHAR(300) NULL,
    BusinessUnit              NVARCHAR(200) NULL,
    TargetRemediationDate     DATE NULL,
    ActualCompletionDate      DATE NULL,
    Notes                     NVARCHAR(2000) NULL,
    LastUpdated               DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    CONSTRAINT UQ_fact_account_progress UNIQUE (AccountKey)
);

-- Snapshot history for trend reporting in Power BI (populated by a
-- scheduled proc -- see 04_BlueTrack_PowerBI_Support.sql).
IF OBJECT_ID('dbo.fact_account_progress_history', 'U') IS NOT NULL DROP TABLE dbo.fact_account_progress_history;
CREATE TABLE fact_account_progress_history (
    HistoryKey             BIGINT IDENTITY(1,1) PRIMARY KEY,
    SnapshotDateKey          INT    NOT NULL REFERENCES dim_date(DateKey),
    AccountKey                BIGINT NOT NULL REFERENCES fact_account(AccountKey),
    StageKey                   INT    NOT NULL REFERENCES dim_blueprint_stage(StageKey),
    StatusKey                  INT    NOT NULL REFERENCES dim_progress_status(StatusKey),
    RiskLevelKey                INT    NULL REFERENCES dim_risk_level(RiskLevelKey)
);

-- Links a Self-Hosted fact_account row to the Privilege Cloud fact_account
-- row representing the same real-world account. See
-- 03_BlueTrack_AccountReconciliation.sql for the matching logic and
-- the critical assumption behind it -- do not treat a match here as
-- confirmed until IsConfirmed = 1.
IF OBJECT_ID('dbo.account_reconciliation', 'U') IS NOT NULL DROP TABLE dbo.account_reconciliation;
CREATE TABLE account_reconciliation (
    ReconciliationKey       BIGINT IDENTITY(1,1) PRIMARY KEY,
    LegacyAccountKey          BIGINT NOT NULL REFERENCES fact_account(AccountKey),
    CurrentAccountKey         BIGINT NOT NULL REFERENCES fact_account(AccountKey),
    MatchMethod                NVARCHAR(50) NOT NULL,
    MatchConfidence             NVARCHAR(20) NOT NULL,
    MatchedDate                 DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
    IsConfirmed                 BIT NOT NULL DEFAULT 0,
    ReviewedBy                  NVARCHAR(200) NULL,
    ReviewedDate                 DATETIME2 NULL,
    RejectedFlag                 BIT NOT NULL DEFAULT 0,
    Notes                        NVARCHAR(1000) NULL,
    CONSTRAINT UQ_account_reconciliation UNIQUE (LegacyAccountKey, CurrentAccountKey)
);

CREATE INDEX IX_account_reconciliation_legacy  ON account_reconciliation(LegacyAccountKey);
CREATE INDEX IX_account_reconciliation_current ON account_reconciliation(CurrentAccountKey);

PRINT 'BlueTrack database and schema created successfully.';
