/* ============================================================================
   47_BlueTrack_AccountDeletion.sql

   RUN THIS AFTER 01-46. Guarded (COL_LENGTH / OBJECT_ID / NOT EXISTS
   checks; CREATE OR ALTER) -- safe to re-run.

   D-185: delete and undelete accounts in BlueTrack, always with a reason.
     fact_account.IsDeletedInSource   what CyberArk says (the load writes it).
                                      Backfilled from IsDeleted: every
                                      deletion so far came from CyberArk.
     fact_account.IsDeleted           unchanged meaning for every reader:
                                      deleted in CyberArk OR in BlueTrack.
     web.account_deletion             one row per account deleted in
                                      BlueTrack: who, when, why. Undelete
                                      removes it (BlueTrack deletes only).
     web.account_deletion_history     every delete and undelete, with its
                                      reason (append-only).
     DeleteAccounts                   new permission, granted to Admin.
     'AccountDeleted' / 'AccountUndeleted'  audit event types.
   usp_Load_FactAccount (46) is redefined to write IsDeletedInSource and
   then set IsDeleted from both, so a BlueTrack delete survives the
   nightly load.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('dbo.fact_account', 'IsDeletedInSource') IS NULL
BEGIN
    ALTER TABLE dbo.fact_account ADD IsDeletedInSource BIT NOT NULL CONSTRAINT DF_fact_account_IsDeletedInSource DEFAULT 0;
END
GO
-- Separate batch: the column must exist before it's referenced.
IF NOT EXISTS (SELECT 1 FROM sys.objects WHERE name = 'account_deletion' AND schema_id = SCHEMA_ID('web'))
    UPDATE dbo.fact_account SET IsDeletedInSource = IsDeleted WHERE IsDeleted = 1;
GO

IF OBJECT_ID('web.account_deletion', 'U') IS NULL
BEGIN
    CREATE TABLE web.account_deletion (
        AccountKey     BIGINT          NOT NULL PRIMARY KEY REFERENCES dbo.fact_account(AccountKey),
        DeletedBy      INT             NOT NULL REFERENCES web.app_user(UserKey),
        DeletedAt      DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME(),
        Reason         NVARCHAR(1000)  NOT NULL
    );
END
GO

IF OBJECT_ID('web.account_deletion_history', 'U') IS NULL
BEGIN
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
END
GO

IF NOT EXISTS (SELECT 1 FROM web.app_permission WHERE PermissionName = 'DeleteAccounts')
    INSERT INTO web.app_permission (PermissionName, Description)
    VALUES ('DeleteAccounts', 'Delete and undelete accounts in BlueTrack (a reason is always required)');
GO

DECLARE @AdminRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');
DECLARE @DeleteAccountsKey INT = (SELECT PermissionKey FROM web.app_permission WHERE PermissionName = 'DeleteAccounts');
IF @AdminRoleKey IS NOT NULL AND NOT EXISTS (SELECT 1 FROM web.role_permission WHERE RoleKey = @AdminRoleKey AND PermissionKey = @DeleteAccountsKey)
    INSERT INTO web.role_permission (RoleKey, PermissionKey) VALUES (@AdminRoleKey, @DeleteAccountsKey);
GO

IF NOT EXISTS (SELECT 1 FROM web.dim_audit_event_type WHERE EventTypeName = 'AccountDeleted')
    INSERT INTO web.dim_audit_event_type (EventTypeName, Description) VALUES ('AccountDeleted', 'An account was deleted in BlueTrack (with a reason)');
IF NOT EXISTS (SELECT 1 FROM web.dim_audit_event_type WHERE EventTypeName = 'AccountUndeleted')
    INSERT INTO web.dim_audit_event_type (EventTypeName, Description) VALUES ('AccountUndeleted', 'An account deleted in BlueTrack was restored (with a reason)');
GO

CREATE OR ALTER PROCEDURE usp_Load_FactAccount
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');

    -- ---- Privilege Cloud ----
    MERGE fact_account AS tgt
    USING (
        SELECT
            @PrivCloudKey AS SourceSystemKey,
            a.AccountID AS SourceAccountId, a.AccountName, a.Address, a.UserName,
            plat.PlatformKey, sf.SafeKey,
            a.SecretType, a.AutoManaged, a.CPMStatus, a.ManualReason,
            a.LastCPMModified AS LastCPMModifiedDate, a.LastReconciled AS LastReconciledDate,
            a.LastVerified AS LastVerifiedDate, a.RemoteAccessRestricted,
            ISNULL(a.Deleted, 0) AS IsDeleted, a.Created AS CreatedDate, a.Platform_LogonDomain AS PlatformLogonDomain
        FROM stg_pc_accounts a
        LEFT JOIN dim_platform plat ON plat.SourceSystemKey = @PrivCloudKey AND plat.PlatformID = a.PlatformID
        LEFT JOIN dim_safe sf ON sf.SourceSystemKey = @PrivCloudKey AND sf.SafeName = a.SafeName
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceAccountId = src.SourceAccountId
    WHEN MATCHED THEN
        UPDATE SET AccountName = src.AccountName, Address = src.Address, UserName = src.UserName,
                   PlatformKey = src.PlatformKey, SafeKey = src.SafeKey, SecretType = src.SecretType,
                   AutoManaged = src.AutoManaged, CPMStatus = src.CPMStatus, ManualReason = src.ManualReason,
                   LastCPMModifiedDate = src.LastCPMModifiedDate, LastReconciledDate = src.LastReconciledDate,
                   LastVerifiedDate = src.LastVerifiedDate, RemoteAccessRestricted = src.RemoteAccessRestricted,
                   IsDeletedInSource = src.IsDeleted, PlatformLogonDomain = src.PlatformLogonDomain
    -- D-184: an account the export already marks as deleted is never added;
    -- one BlueTrack already has is kept and flagged (WHEN MATCHED above).
    WHEN NOT MATCHED BY TARGET AND src.IsDeleted = 0 THEN
        INSERT (SourceSystemKey, SourceAccountId, AccountName, Address, UserName, PlatformKey, SafeKey,
                SecretType, AutoManaged, CPMStatus, ManualReason, LastCPMModifiedDate, LastReconciledDate,
                LastVerifiedDate, RemoteAccessRestricted, IsDeleted, CreatedDate, PlatformLogonDomain)
        VALUES (src.SourceSystemKey, src.SourceAccountId, src.AccountName, src.Address, src.UserName,
                src.PlatformKey, src.SafeKey, src.SecretType, src.AutoManaged, src.CPMStatus, src.ManualReason,
                src.LastCPMModifiedDate, src.LastReconciledDate, src.LastVerifiedDate, src.RemoteAccessRestricted,
                src.IsDeleted, src.CreatedDate, src.PlatformLogonDomain)
    -- an account present in fact_account for this source but absent from the
    -- latest staging snapshot means it no longer exists in the vault -- flag it
    WHEN NOT MATCHED BY SOURCE AND tgt.SourceSystemKey = @PrivCloudKey THEN
        UPDATE SET IsDeletedInSource = 1;

    -- ---- Self-Hosted ----
    ;WITH PivotedAccounts AS (
        SELECT
            f.CAFSafeID, f.CAFFileID, f.CAFSafeName,
            CAST(f.CAFSafeID AS NVARCHAR(50)) + '_' + CAST(f.CAFFileID AS NVARCHAR(50)) AS SourceAccountId,
            f.CAFFileName AS AccountName,
            f.CAFCreationDate AS CreatedDate,
            f.CAFModificationDate AS LastCPMModifiedDate,
            f.CAFLastUsedDate AS LastVerifiedDate,
            -- D-184: deleted when CAFDeletionDate is a real date: after the
            -- epoch placeholder some versions write (1970-01-01, or 1900 /
            -- 0001 defaults), and not before the account was created.
            CASE WHEN f.CAFDeletionDate >= '19700102'
                      AND (f.CAFCreationDate IS NULL OR f.CAFDeletionDate >= f.CAFCreationDate)
                 THEN 1 ELSE 0 END AS IsDeleted,
            -- PLACEHOLDER property names -- confirm against stg_sh_objectproperties before trusting
            MAX(CASE WHEN op.CAOPObjectPropertyName = 'UserName' THEN op.CAOPObjectPropertyValue END) AS UserName,
            MAX(CASE WHEN op.CAOPObjectPropertyName = 'Address'  THEN op.CAOPObjectPropertyValue END) AS Address,
            MAX(CASE WHEN op.CAOPObjectPropertyName = 'PolicyID' THEN op.CAOPObjectPropertyValue END) AS PolicyID
        FROM stg_sh_files f
        LEFT JOIN stg_sh_objectproperties op
               ON op.CAOPFileId = f.CAFFileID AND op.CAOPSafeId = f.CAFSafeID
        WHERE f.CAFType = 2   -- 'Password' per dim_selfhosted_code (CodeType 12, CodeValue 2)
        GROUP BY f.CAFSafeID, f.CAFFileID, f.CAFSafeName, f.CAFFileName,
                 f.CAFCreationDate, f.CAFModificationDate, f.CAFLastUsedDate, f.CAFDeletionDate
    )
    MERGE fact_account AS tgt
    USING (
        SELECT @SelfHostedKey AS SourceSystemKey, p.SourceAccountId, p.AccountName, p.Address, p.UserName,
               plat.PlatformKey, sf.SafeKey, p.CreatedDate, p.LastCPMModifiedDate, p.LastVerifiedDate, p.IsDeleted
        FROM PivotedAccounts p
        LEFT JOIN dim_safe sf ON sf.SourceSystemKey = @SelfHostedKey AND sf.SafeUrlId = CAST(p.CAFSafeID AS NVARCHAR(300))
        LEFT JOIN dim_platform plat ON plat.SourceSystemKey = @SelfHostedKey AND plat.PlatformID = p.PolicyID  -- will stay NULL until a Self-Hosted platform reference exists (see schema notes)
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceAccountId = src.SourceAccountId
    WHEN MATCHED THEN
        UPDATE SET AccountName = src.AccountName, Address = src.Address, UserName = src.UserName,
                   PlatformKey = src.PlatformKey, SafeKey = src.SafeKey,
                   LastCPMModifiedDate = src.LastCPMModifiedDate, LastVerifiedDate = src.LastVerifiedDate,
                   IsDeletedInSource = src.IsDeleted
    WHEN NOT MATCHED BY TARGET AND src.IsDeleted = 0 THEN
        INSERT (SourceSystemKey, SourceAccountId, AccountName, Address, UserName, PlatformKey, SafeKey,
                CreatedDate, LastCPMModifiedDate, LastVerifiedDate, IsDeleted)
        VALUES (src.SourceSystemKey, src.SourceAccountId, src.AccountName, src.Address, src.UserName,
                src.PlatformKey, src.SafeKey, src.CreatedDate, src.LastCPMModifiedDate, src.LastVerifiedDate, 0)
    WHEN NOT MATCHED BY SOURCE AND tgt.SourceSystemKey = @SelfHostedKey THEN
        UPDATE SET IsDeletedInSource = 1;

    -- D-185: IsDeleted -- the flag every list, report and view filters on --
    -- is deleted in CyberArk OR deleted in BlueTrack (web.account_deletion),
    -- so a BlueTrack delete survives the nightly load and an undelete
    -- returns the account to whatever CyberArk says.
    UPDATE fa
    SET IsDeleted = CASE WHEN fa.IsDeletedInSource = 1 OR ad.AccountKey IS NOT NULL THEN 1 ELSE 0 END
    FROM fact_account fa
    LEFT JOIN web.account_deletion ad ON ad.AccountKey = fa.AccountKey
    WHERE fa.SourceSystemKey IN (@PrivCloudKey, @SelfHostedKey)
      AND fa.IsDeleted <> CASE WHEN fa.IsDeletedInSource = 1 OR ad.AccountKey IS NOT NULL THEN 1 ELSE 0 END;
END
GO

PRINT '47_BlueTrack_AccountDeletion.sql complete.';
