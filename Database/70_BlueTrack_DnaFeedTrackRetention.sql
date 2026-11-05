/* ============================================================================
   70_BlueTrack_DnaFeedTrackRetention.sql

   RUN THIS AFTER 01-69. Guarded -- safe to re-run.

   The user's requests of 2026-11-05 for DNA (Discovery & Audit, D-325):
   - D-341: DNA's newer report format (Windows Scan, Unix Scan, Domain Scan,
     Database Scan) is read, with the columns it adds: web.dna_account gets
     machine type, OS version, description, threat cause, pass-the-hash,
     service account type and description, SPN, roles, permissions, details
     and the local mapped account. A data feed can import the workbook from
     a folder each night (feed type DnaReport).
   - D-342: a DNA account can be tracked in BlueTrack: it becomes an account
     (source DNA) at the first stage. web.dna_track_rule and its conditions
     track the accounts they match after each import.
   - D-343: DNA data can be purged (all of it, now), and
     web.app_config.DnaRetentionDays deletes imports older than that each
     night (NULL keeps them).
   - D-344: web.dna_column_setting marks the DNA columns that only holders
     of the new permission ViewDnaSensitiveColumns (Admin) may see; the API
     blanks them for anyone else.
   - D-345: the workbook's hard-coded credentials sheet goes into
     web.dna_hard_coded_credential, for Reports > DNA Hard-Coded Credentials
     (ViewDnaSensitiveColumns only). DNA masks the credential in its
     "Hard-Coded Credential" column (the file's line, with the password as
     asterisks); the import masks again any password-style value still
     readable, so no password is stored.
   ============================================================================ */

USE $DatabaseName$;
GO

-- D-341: the newer format's columns.
IF COL_LENGTH('web.dna_account', 'MachineType') IS NULL
    ALTER TABLE web.dna_account ADD
        MachineType               NVARCHAR(200)  NULL,
        OsVersion                 NVARCHAR(300)  NULL,
        AccountDescription        NVARCHAR(1000) NULL,
        ThreatCause               NVARCHAR(500)  NULL,
        PassTheHashVulnerable     NVARCHAR(50)   NULL,
        PassTheHashHashFound      NVARCHAR(50)   NULL,
        ServiceAccountType        NVARCHAR(300)  NULL,
        ServiceAccountDescription NVARCHAR(1000) NULL,
        SpnDescription            NVARCHAR(1000) NULL,
        Roles                     NVARCHAR(2000) NULL,
        DbPermissions             NVARCHAR(2000) NULL,
        Details                   NVARCHAR(2000) NULL,
        LocalMappedAccount        NVARCHAR(300)  NULL;
GO

-- D-342: the source of the accounts tracked from DNA.
IF NOT EXISTS (SELECT 1 FROM dbo.dim_source_system WHERE SourceSystemCode = 'DNA')
    INSERT INTO dbo.dim_source_system (SourceSystemCode, SourceSystemName) VALUES ('DNA', 'CyberArk DNA');
GO

-- D-341: a feed that imports a DNA workbook.
IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_data_feed_FeedType' AND definition NOT LIKE '%DnaReport%')
    ALTER TABLE web.data_feed DROP CONSTRAINT CK_data_feed_FeedType;
GO
IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_data_feed_FeedType')
    ALTER TABLE web.data_feed ADD CONSTRAINT CK_data_feed_FeedType CHECK (FeedType IN (
        'TargetInventory', 'AccessGroupInventory', 'AccessGroupTargetMap',
        'AccountAccessGroupMembership', 'AccountTargetMap', 'Applications', 'SafeAssignments',
        'AccountInventory', 'AccountAssignments', 'DnaReport'));
GO

-- D-343: how long DNA imports are kept (NULL: until the next import replaces them, or a purge).
IF COL_LENGTH('web.app_config', 'DnaRetentionDays') IS NULL
    ALTER TABLE web.app_config ADD DnaRetentionDays INT NULL
        CONSTRAINT CK_app_config_DnaRetentionDays CHECK (DnaRetentionDays BETWEEN 1 AND 3650);
GO

-- D-344: the DNA columns only ViewDnaSensitiveColumns may see.
IF OBJECT_ID('web.dna_column_setting', 'U') IS NULL
    CREATE TABLE web.dna_column_setting (
        ColumnName  NVARCHAR(100) NOT NULL CONSTRAINT PK_dna_column_setting PRIMARY KEY,
        IsRestricted BIT          NOT NULL CONSTRAINT DF_dna_column_setting_IsRestricted DEFAULT 0
    );
GO

-- D-342: rules that track DNA accounts after each import; a rule matches when all its conditions do.
IF OBJECT_ID('web.dna_track_rule', 'U') IS NULL
    CREATE TABLE web.dna_track_rule (
        DnaTrackRuleKey INT IDENTITY(1,1) CONSTRAINT PK_dna_track_rule PRIMARY KEY,
        RuleName        NVARCHAR(200)  NOT NULL CONSTRAINT UQ_dna_track_rule_RuleName UNIQUE,
        IsEnabled       BIT            NOT NULL CONSTRAINT DF_dna_track_rule_IsEnabled DEFAULT 1,
        Description     NVARCHAR(1000) NULL,
        ModifiedBy      INT            NULL REFERENCES web.app_user(UserKey),
        ModifiedAt      DATETIME2      NOT NULL CONSTRAINT DF_dna_track_rule_ModifiedAt DEFAULT SYSUTCDATETIME()
    );
GO
IF OBJECT_ID('web.dna_track_rule_condition', 'U') IS NULL
    CREATE TABLE web.dna_track_rule_condition (
        DnaTrackRuleKey INT           NOT NULL REFERENCES web.dna_track_rule(DnaTrackRuleKey) ON DELETE CASCADE,
        ConditionOrder  INT           NOT NULL,
        FieldName       NVARCHAR(50)  NOT NULL,
        Operator        NVARCHAR(20)  NOT NULL,
        Value           NVARCHAR(400) NULL,
        CONSTRAINT PK_dna_track_rule_condition PRIMARY KEY (DnaTrackRuleKey, ConditionOrder)
    );
GO

-- D-345: the hard-coded credentials DNA found, replaced with each import like the accounts.
IF OBJECT_ID('web.dna_hard_coded_credential', 'U') IS NULL
    CREATE TABLE web.dna_hard_coded_credential (
        DnaHardCodedKey     INT IDENTITY(1,1) CONSTRAINT PK_dna_hard_coded_credential PRIMARY KEY,
        DnaImportKey        INT            NOT NULL REFERENCES web.dna_import(DnaImportKey),
        SheetCode           NVARCHAR(50)   NOT NULL,
        MachineName         NVARCHAR(300)  NULL,
        MachineType         NVARCHAR(200)  NULL,
        ApplicationServer   NVARCHAR(300)  NULL,
        ApplicationName     NVARCHAR(300)  NULL,
        SiteName            NVARCHAR(300)  NULL,
        AccountName         NVARCHAR(300)  NULL,
        HardCodedInFile     NVARCHAR(1000) NULL,
        CredentialLocation  NVARCHAR(2000) NULL,   -- DNA's masked "Hard-Coded Credential": the line and its format, never the password
        PasswordLength      INT            NULL,
        TargetSystemAddress NVARCHAR(300)  NULL,
        TargetSystemType    NVARCHAR(200)  NULL,
        OsVersion           NVARCHAR(300)  NULL,
        Details             NVARCHAR(2000) NULL
    );
GO
IF COL_LENGTH('web.dna_import', 'HardCodedCount') IS NULL
    ALTER TABLE web.dna_import ADD HardCodedCount INT NULL;
GO

-- D-342: who tracked a DNA account, and how.
IF COL_LENGTH('web.dna_account', 'TrackedBy') IS NULL
    ALTER TABLE web.dna_account ADD TrackedBy NVARCHAR(250) NULL;   -- a person's name, or "Rule: <name>"; NULL when matched at import
GO

-- D-347: DNA couldn't resolve the account: its name is its SID (a trusted domain's account, or a deleted one).
IF COL_LENGTH('web.dna_account', 'IsUnresolvedSid') IS NULL
    ALTER TABLE web.dna_account ADD IsUnresolvedSid BIT NOT NULL CONSTRAINT DF_dna_account_IsUnresolvedSid DEFAULT 0;
GO

INSERT INTO web.app_permission (PermissionName, Description)
SELECT v.PermissionName, v.Description
FROM (VALUES
    ('ViewDnaSensitiveColumns', 'See the DNA account columns marked as needing it on Admin > Data Sources (D-344)')
) v(PermissionName, Description)
WHERE NOT EXISTS (SELECT 1 FROM web.app_permission p WHERE p.PermissionName = v.PermissionName);
GO
INSERT INTO web.role_permission (RoleKey, PermissionKey)
SELECT r.AppRoleKey, p.PermissionKey
FROM web.app_role r
JOIN web.app_permission p ON r.RoleName = 'Admin' AND p.PermissionName = 'ViewDnaSensitiveColumns'
WHERE NOT EXISTS (SELECT 1 FROM web.role_permission rp WHERE rp.RoleKey = r.AppRoleKey AND rp.PermissionKey = p.PermissionKey);
GO

INSERT INTO web.dim_audit_event_type (EventTypeName, Description)
SELECT v.EventTypeName, v.Description
FROM (VALUES
    ('DnaPurged',             'DNA data was purged: by an admin, or by the retention period (D-343)'),
    ('DnaAccountsTracked',    'DNA accounts were tracked in BlueTrack: by a person or by a DNA tracking rule (D-342)'),
    ('DnaSettingsChanged',    'The DNA settings changed: the retention period or the columns needing ViewDnaSensitiveColumns (D-343, D-344)'),
    ('DnaTrackRuleChanged',   'A DNA tracking rule was created, changed or deleted (D-342)')
) v(EventTypeName, Description)
WHERE NOT EXISTS (SELECT 1 FROM web.dim_audit_event_type t WHERE t.EventTypeName = v.EventTypeName);
GO

PRINT '70_BlueTrack_DnaFeedTrackRetention.sql complete.';
GO
