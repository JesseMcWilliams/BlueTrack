/* ============================================================================
   25_BlueTrack_AccountClassification.sql

   RUN THIS AFTER 01-24. Guarded -- safe to re-run.

   D-233: account classification on Reports > Discovered Accounts, decided by
   the user on 2026-10-14. Each discovered account gets an Account Class
   (Primary, Secondary, Admin or Unknown), an Owner (the person's primary
   account) and how both were found, from rules an admin sets on
   Admin > Account Classification. Nothing changes until an admin turns it
   on: the settings row starts disabled and no rules are seeded.

     web.account_classification_config  one row: IsEnabled (off), the class
                                         when no rule matches (Unknown or
                                         Primary), and owner by employee ID
                                         (off; employeeID or employeeNumber).
     web.account_classification_rule    the rules. RuleType Attribute (an
                                         allow-listed AD attribute: equals,
                                         contains, starts with, is set), Name
                                         (sAMAccountName or userPrincipalName:
                                         starts with, ends with, wildcard
                                         pattern) or OU (distinguished name
                                         contains / ends with). Attribute
                                         rules run first, then Name, then OU,
                                         each in DisplayOrder; the first
                                         enabled match wins.
     web.discovered_account             new columns. What discovery reads for
                                         the rules (UserPrincipalName,
                                         EmployeeId, EmployeeNumber and the
                                         allow-listed attributes as a JSON
                                         object in DirectoryAttributes), the
                                         result (AccountClass, ClassSource,
                                         ClassRuleKey, OwnerSamAccountName,
                                         OwnerDisplayName, OwnerSource,
                                         ClassifiedDate) and the manual
                                         override (ClassOverride,
                                         OwnerOverrideSamAccountName,
                                         OverrideBy, OverrideDate), which the
                                         discovery upsert never touches.
     web.ad_owner_lookup                accounts read from AD only to find
                                         owners (a stripped name or a shared
                                         employee ID that isn't itself a
                                         discovered account); replaced per
                                         domain on each discovery run, so a
                                         reclassify needs no LDAP.
     AccountClassification              audit event type: settings, rules,
                                         reclassify and per-row overrides.
   ============================================================================ */

USE $DatabaseName$;
GO

IF OBJECT_ID('web.account_classification_config') IS NULL
BEGIN
    CREATE TABLE web.account_classification_config (
        AccountClassificationConfigKey INT NOT NULL CONSTRAINT PK_account_classification_config PRIMARY KEY
            CONSTRAINT CK_account_classification_config_SingleRow CHECK (AccountClassificationConfigKey = 1),
        IsEnabled               BIT           NOT NULL CONSTRAINT DF_account_classification_config_IsEnabled DEFAULT 0,
        DefaultAccountClass     NVARCHAR(20)  NOT NULL CONSTRAINT DF_account_classification_config_DefaultAccountClass DEFAULT 'Unknown'
            CONSTRAINT CK_account_classification_config_DefaultAccountClass CHECK (DefaultAccountClass IN ('Unknown', 'Primary')),
        OwnerByEmployeeId       BIT           NOT NULL CONSTRAINT DF_account_classification_config_OwnerByEmployeeId DEFAULT 0,
        EmployeeIdAttribute     NVARCHAR(20)  NOT NULL CONSTRAINT DF_account_classification_config_EmployeeIdAttribute DEFAULT 'employeeID'
            CONSTRAINT CK_account_classification_config_EmployeeIdAttribute CHECK (EmployeeIdAttribute IN ('employeeID', 'employeeNumber')),
        ModifiedBy              INT           NULL CONSTRAINT FK_account_classification_config_ModifiedBy REFERENCES web.app_user(UserKey),
        ModifiedDate            DATETIME2     NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM web.account_classification_config)
    INSERT INTO web.account_classification_config (AccountClassificationConfigKey) VALUES (1);
GO

IF OBJECT_ID('web.account_classification_rule') IS NULL
BEGIN
    CREATE TABLE web.account_classification_rule (
        AccountClassificationRuleKey INT IDENTITY(1,1) CONSTRAINT PK_account_classification_rule PRIMARY KEY,
        RuleType        NVARCHAR(20)  NOT NULL
            CONSTRAINT CK_account_classification_rule_RuleType CHECK (RuleType IN ('Attribute', 'Name', 'OU')),
        AttributeName   NVARCHAR(64)  NULL,      -- Attribute: the allow-listed attribute; Name: sAMAccountName or userPrincipalName; OU: NULL
        Operator        NVARCHAR(20)  NOT NULL
            CONSTRAINT CK_account_classification_rule_Operator CHECK (Operator IN ('Equals', 'Contains', 'StartsWith', 'EndsWith', 'IsSet', 'Pattern')),
        Value           NVARCHAR(500) NULL,      -- NULL only for IsSet
        AccountClass    NVARCHAR(20)  NOT NULL
            CONSTRAINT CK_account_classification_rule_AccountClass CHECK (AccountClass IN ('Primary', 'Secondary', 'Admin')),
        IsEnabled       BIT           NOT NULL CONSTRAINT DF_account_classification_rule_IsEnabled DEFAULT 1,
        DisplayOrder    INT           NOT NULL CONSTRAINT DF_account_classification_rule_DisplayOrder DEFAULT 0,
        CreatedBy       INT           NULL CONSTRAINT FK_account_classification_rule_CreatedBy REFERENCES web.app_user(UserKey),
        CreatedDate     DATETIME2     NOT NULL CONSTRAINT DF_account_classification_rule_CreatedDate DEFAULT SYSUTCDATETIME(),
        ModifiedBy      INT           NULL CONSTRAINT FK_account_classification_rule_ModifiedBy REFERENCES web.app_user(UserKey),
        ModifiedDate    DATETIME2     NULL
    );
END
GO

IF COL_LENGTH('web.discovered_account', 'UserPrincipalName') IS NULL
    ALTER TABLE web.discovered_account ADD UserPrincipalName NVARCHAR(300) NULL;
GO
IF COL_LENGTH('web.discovered_account', 'EmployeeId') IS NULL
    ALTER TABLE web.discovered_account ADD EmployeeId NVARCHAR(100) NULL;
GO
IF COL_LENGTH('web.discovered_account', 'EmployeeNumber') IS NULL
    ALTER TABLE web.discovered_account ADD EmployeeNumber NVARCHAR(100) NULL;
GO
IF COL_LENGTH('web.discovered_account', 'DirectoryAttributes') IS NULL
    ALTER TABLE web.discovered_account ADD DirectoryAttributes NVARCHAR(MAX) NULL
        CONSTRAINT CK_discovered_account_DirectoryAttributes CHECK (DirectoryAttributes IS NULL OR ISJSON(DirectoryAttributes) = 1);
GO
IF COL_LENGTH('web.discovered_account', 'AttributesReadDate') IS NULL
    ALTER TABLE web.discovered_account ADD AttributesReadDate DATETIME2 NULL;
GO
IF COL_LENGTH('web.discovered_account', 'AccountClass') IS NULL
    ALTER TABLE web.discovered_account ADD AccountClass NVARCHAR(20) NULL;
GO
IF COL_LENGTH('web.discovered_account', 'ClassSource') IS NULL
    ALTER TABLE web.discovered_account ADD ClassSource NVARCHAR(20) NULL;   -- Rule | Default | Manual
GO
IF COL_LENGTH('web.discovered_account', 'ClassRuleKey') IS NULL
    ALTER TABLE web.discovered_account ADD ClassRuleKey INT NULL;          -- no FK: a deleted rule is cleared by the reclassify that follows
GO
IF COL_LENGTH('web.discovered_account', 'OwnerSamAccountName') IS NULL
    ALTER TABLE web.discovered_account ADD OwnerSamAccountName NVARCHAR(300) NULL;
GO
IF COL_LENGTH('web.discovered_account', 'OwnerDisplayName') IS NULL
    ALTER TABLE web.discovered_account ADD OwnerDisplayName NVARCHAR(300) NULL;
GO
IF COL_LENGTH('web.discovered_account', 'OwnerSource') IS NULL
    ALTER TABLE web.discovered_account ADD OwnerSource NVARCHAR(20) NULL;  -- Self | Name | EmployeeId | Ambiguous | Manual
GO
IF COL_LENGTH('web.discovered_account', 'ClassifiedDate') IS NULL
    ALTER TABLE web.discovered_account ADD ClassifiedDate DATETIME2 NULL;
GO
IF COL_LENGTH('web.discovered_account', 'ClassOverride') IS NULL
    ALTER TABLE web.discovered_account ADD ClassOverride NVARCHAR(20) NULL
        CONSTRAINT CK_discovered_account_ClassOverride CHECK (ClassOverride IS NULL OR ClassOverride IN ('Primary', 'Secondary', 'Admin', 'Unknown'));
GO
IF COL_LENGTH('web.discovered_account', 'OwnerOverrideSamAccountName') IS NULL
    ALTER TABLE web.discovered_account ADD OwnerOverrideSamAccountName NVARCHAR(300) NULL;
GO
IF COL_LENGTH('web.discovered_account', 'OverrideBy') IS NULL
    ALTER TABLE web.discovered_account ADD OverrideBy INT NULL
        CONSTRAINT FK_discovered_account_OverrideBy REFERENCES web.app_user(UserKey);
GO
IF COL_LENGTH('web.discovered_account', 'OverrideDate') IS NULL
    ALTER TABLE web.discovered_account ADD OverrideDate DATETIME2 NULL;
GO

IF OBJECT_ID('web.ad_owner_lookup') IS NULL
BEGIN
    CREATE TABLE web.ad_owner_lookup (
        DomainName          NVARCHAR(100)  NOT NULL,
        SamAccountName      NVARCHAR(300)  NOT NULL,
        UserPrincipalName   NVARCHAR(300)  NULL,
        DistinguishedName   NVARCHAR(1000) NULL,
        DisplayName         NVARCHAR(300)  NULL,
        IsEnabled           BIT            NOT NULL,
        EmployeeId          NVARCHAR(100)  NULL,
        EmployeeNumber      NVARCHAR(100)  NULL,
        DirectoryAttributes NVARCHAR(MAX)  NULL
            CONSTRAINT CK_ad_owner_lookup_DirectoryAttributes CHECK (DirectoryAttributes IS NULL OR ISJSON(DirectoryAttributes) = 1),
        ReadDate            DATETIME2      NOT NULL CONSTRAINT DF_ad_owner_lookup_ReadDate DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_ad_owner_lookup PRIMARY KEY (DomainName, SamAccountName)
    );
END
GO

INSERT INTO web.dim_audit_event_type (EventTypeName, Description)
SELECT 'AccountClassification', 'Account classification settings, rules, reclassify or a discovered account''s class/owner override changed'
WHERE NOT EXISTS (SELECT 1 FROM web.dim_audit_event_type WHERE EventTypeName = 'AccountClassification');
GO
