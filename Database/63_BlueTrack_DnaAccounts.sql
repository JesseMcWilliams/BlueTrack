/* ============================================================================
   63_BlueTrack_DnaAccounts.sql

   RUN THIS AFTER 01-62. Guarded -- safe to re-run.

   - D-325: CyberArk Discovery & Audit (DNA) as a data source. An admin
     uploads DNA's account-list workbook on Admin > Data Sources; each
     account row of its finding sheets (non-compliant, N/A-compliance, SPN
     and database accounts) becomes a web.dna_account row, and each upload
     replaces the one before (it is the latest scan). The workbook's
     hard-coded credentials sheet (HCC) and any column of credentials are
     never read, so nothing secret is stored.
   - web.dna_import: one row per upload (who, when, the file name, how many
     accounts per sheet), kept as the import history.
   - TrackedAccountKey: the tracked account (fact_account) it matched at
     import, by user name, and for a local or database account also by
     machine; NULL means BlueTrack doesn't track it yet.
   ============================================================================ */

USE $DatabaseName$;
GO

IF OBJECT_ID('web.dna_import', 'U') IS NULL
    CREATE TABLE web.dna_import (
        DnaImportKey  INT IDENTITY(1,1) PRIMARY KEY,
        FileName      NVARCHAR(260)  NOT NULL,
        ImportedAt    DATETIME2      NOT NULL CONSTRAINT DF_dna_import_ImportedAt DEFAULT SYSUTCDATETIME(),
        ImportedBy    INT            NULL REFERENCES web.app_user(UserKey),
        AccountCount  INT            NOT NULL,
        SheetCounts   NVARCHAR(2000) NULL      -- "NC-DA=12; NC-LA=40; ..." in the workbook's order
    );
GO

IF OBJECT_ID('web.dna_account', 'U') IS NULL
    CREATE TABLE web.dna_account (
        DnaAccountKey          INT IDENTITY(1,1) PRIMARY KEY,
        DnaImportKey           INT            NOT NULL REFERENCES web.dna_import(DnaImportKey),
        SheetCode              NVARCHAR(50)   NOT NULL,   -- the finding: the workbook's sheet (NC-DA, NAC-LA-D, DS-SPN, DB-P-SRV, ...)
        IsDomainAccount        BIT            NOT NULL,
        MachineName            NVARCHAR(300)  NULL,
        AccountName            NVARCHAR(300)  NOT NULL,
        DisplayName            NVARCHAR(300)  NULL,
        AccountType            NVARCHAR(200)  NULL,
        AccountCategory        NVARCHAR(200)  NULL,
        AccountGroup           NVARCHAR(500)  NULL,
        PrivilegedDomainGroup  NVARCHAR(500)  NULL,
        InstanceName           NVARCHAR(300)  NULL,
        DatabaseName           NVARCHAR(300)  NULL,
        ComplianceStatus       NVARCHAR(200)  NULL,
        AccountState           NVARCHAR(200)  NULL,
        PasswordNeverExpires   BIT            NULL,
        PasswordAgeDays        INT            NULL,
        PasswordLastSet        DATETIME2      NULL,
        LastLoginDate          DATETIME2      NULL,
        AccountExpirationDate  DATETIME2      NULL,
        TrackedAccountKey      BIGINT         NULL
    );
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_dna_account_Sheet' AND object_id = OBJECT_ID('web.dna_account'))
    CREATE INDEX IX_dna_account_Sheet ON web.dna_account (SheetCode, AccountName);
GO

INSERT INTO web.dim_audit_event_type (EventTypeName, Description)
SELECT v.EventTypeName, v.Description
FROM (VALUES
    ('DnaImported', 'A DNA (Discovery & Audit) account-list workbook was imported, replacing the previous one (D-325)')
) v(EventTypeName, Description)
WHERE NOT EXISTS (SELECT 1 FROM web.dim_audit_event_type t WHERE t.EventTypeName = v.EventTypeName);
GO

PRINT '63_BlueTrack_DnaAccounts.sql complete.';
GO
