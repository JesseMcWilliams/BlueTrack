/* ============================================================================
   36_BlueTrack_SelfHostedPlatformsAndProperties.sql

   RUN THIS AFTER 01-35. Guarded -- safe to re-run.

   D-244, from the real-data findings of 2026-10-13 (Planning_Backlog.md):

   - Self-Hosted platforms. A Self-Hosted account's platform is its PolicyID
     property, but there is no Self-Hosted platform list, so dim_platform had
     none and every Self-Hosted account had a NULL PlatformKey.
     usp_Load_DimPlatform now also adds a dim_platform row (PlatformName =
     PlatformID = the PolicyID) for each distinct PolicyID in the staged
     object properties of the accounts it would import. Insert-only: a policy
     no account uses any more stays (accounts and platform_account_type_map
     may point at it). The Platform Review (vw_review_platform_sor_accounttype)
     lists them unmapped, to be given an account type and source of record;
     new accounts then get those as defaults (usp_Load_FactAccountProgress).
   - usp_Load_FactAccount reads two more Self-Hosted properties:
       LogonDomain  -> PlatformLogonDomain (as Privilege Cloud's is);
       CPMDisabled  -> AutoManaged = 0 and ManualReason = the reason. No value
                       leaves AutoManaged NULL: the account's platform may not
                       be managed at all, and nothing in the export says.
     It also drops the "PLACEHOLDER" note: UserName, Address and PolicyID were
     confirmed against the real export.
     Not read (nowhere to put them yet): DeviceType, CreationMethod and the
     linked logon/reconcile account properties (ExtraPass1*, ExtraPass3*).
   - Drops dbo.stg_discovered_accounts: empty, and nothing references it (it was
     superseded by web.discovered_account).
   ============================================================================ */

USE $DatabaseName$;
GO

CREATE OR ALTER PROCEDURE usp_Load_DimPlatform
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');
    -- The ignored-safe pattern (D-186) applies to the accounts a platform is learned from.
    DECLARE @IgnoreMode NVARCHAR(10), @IgnoreValue NVARCHAR(200);
    SELECT @IgnoreMode = SafeIgnoreMode, @IgnoreValue = SafeIgnoreValue FROM web.app_config;

    -- ---- Privilege Cloud: its own platform list ----
    MERGE dim_platform AS tgt
    USING (
        SELECT @PrivCloudKey AS SourceSystemKey, PlatformID, Name AS PlatformName, Description,
               Active AS IsActive,
               -- normalize inconsistent casing observed in source ('regular' / 'Regular' / 'group')
               LOWER(LTRIM(RTRIM(PlatformType))) AS PlatformType
        FROM stg_pc_platforms
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.PlatformID = src.PlatformID
    WHEN MATCHED THEN
        UPDATE SET PlatformName = src.PlatformName, Description = src.Description,
                   IsActive = src.IsActive, PlatformType = src.PlatformType
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, PlatformID, PlatformName, Description, IsActive, PlatformType)
        VALUES (src.SourceSystemKey, src.PlatformID, src.PlatformName, src.Description, src.IsActive, src.PlatformType);

    -- ---- Self-Hosted: one platform per distinct PolicyID (D-244), added only ----
    INSERT INTO dim_platform (SourceSystemKey, PlatformID, PlatformName)
    SELECT @SelfHostedKey, policy.PolicyID, policy.PolicyID
    FROM (
        SELECT DISTINCT LEFT(LTRIM(RTRIM(op.CAOPObjectPropertyValue)), 100) AS PolicyID
        FROM stg_sh_objectproperties op
        JOIN stg_sh_files f ON f.CAFFileID = op.CAOPFileId AND f.CAFSafeID = op.CAOPSafeId
        WHERE op.CAOPObjectPropertyName = 'PolicyID'
          AND f.CAFType = 2   -- 'Password', as in usp_Load_FactAccount
          AND dbo.fn_MatchesNamePattern(f.CAFSafeName, @IgnoreMode, @IgnoreValue) = 0
          AND NULLIF(LTRIM(RTRIM(op.CAOPObjectPropertyValue)), '') IS NOT NULL
    ) policy
    WHERE @SelfHostedKey IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM dim_platform p WHERE p.SourceSystemKey = @SelfHostedKey AND p.PlatformID = policy.PolicyID);
END
GO

CREATE OR ALTER PROCEDURE usp_Load_FactAccount
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');
    -- D-186: accounts in a safe matching the ignored-safe pattern aren't
    -- imported; ones already in BlueTrack drop out of the source rows
    -- below, so the load flags them deleted (history kept).
    DECLARE @IgnoreMode NVARCHAR(10), @IgnoreValue NVARCHAR(200);
    SELECT @IgnoreMode = SafeIgnoreMode, @IgnoreValue = SafeIgnoreValue FROM web.app_config;

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
        WHERE dbo.fn_MatchesNamePattern(a.SafeName, @IgnoreMode, @IgnoreValue) = 0
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
            -- Property names confirmed against the real EVD export (2026-10-13). D-244: LogonDomain and CPMDisabled
            -- are read too (CPMDisabled: "if the account is disabled for automatic management, the reason why",
            -- PAM Self-Hosted web services reference, Get Account Details).
            MAX(CASE WHEN op.CAOPObjectPropertyName = 'UserName' THEN op.CAOPObjectPropertyValue END) AS UserName,
            MAX(CASE WHEN op.CAOPObjectPropertyName = 'Address'  THEN op.CAOPObjectPropertyValue END) AS Address,
            MAX(CASE WHEN op.CAOPObjectPropertyName = 'PolicyID' THEN op.CAOPObjectPropertyValue END) AS PolicyID,
            MAX(CASE WHEN op.CAOPObjectPropertyName = 'LogonDomain' THEN op.CAOPObjectPropertyValue END) AS LogonDomain,
            MAX(CASE WHEN op.CAOPObjectPropertyName = 'CPMDisabled' THEN op.CAOPObjectPropertyValue END) AS CPMDisabled
        FROM stg_sh_files f
        LEFT JOIN stg_sh_objectproperties op
               ON op.CAOPFileId = f.CAFFileID AND op.CAOPSafeId = f.CAFSafeID
        WHERE f.CAFType = 2   -- 'Password' per dim_selfhosted_code (CodeType 12, CodeValue 2)
          AND dbo.fn_MatchesNamePattern(f.CAFSafeName, @IgnoreMode, @IgnoreValue) = 0
        GROUP BY f.CAFSafeID, f.CAFFileID, f.CAFSafeName, f.CAFFileName,
                 f.CAFCreationDate, f.CAFModificationDate, f.CAFLastUsedDate, f.CAFDeletionDate
    )
    MERGE fact_account AS tgt
    USING (
        SELECT @SelfHostedKey AS SourceSystemKey, p.SourceAccountId, p.AccountName, p.Address, p.UserName,
               plat.PlatformKey, sf.SafeKey, p.CreatedDate, p.LastCPMModifiedDate, p.LastVerifiedDate, p.IsDeleted,
               NULLIF(LTRIM(RTRIM(p.LogonDomain)), '') AS PlatformLogonDomain,
               -- D-244: CPMDisabled holds the reason when automatic management is off, so a value means not auto-managed;
               -- no value says nothing (the platform may not be managed at all), so AutoManaged stays NULL.
               CASE WHEN NULLIF(LTRIM(RTRIM(p.CPMDisabled)), '') IS NOT NULL THEN CAST(0 AS BIT) END AS AutoManaged,
               LEFT(NULLIF(LTRIM(RTRIM(p.CPMDisabled)), ''), 500) AS ManualReason
        FROM PivotedAccounts p
        LEFT JOIN dim_safe sf ON sf.SourceSystemKey = @SelfHostedKey AND sf.SafeUrlId = CAST(p.CAFSafeID AS NVARCHAR(300))
        LEFT JOIN dim_platform plat ON plat.SourceSystemKey = @SelfHostedKey AND plat.PlatformID = p.PolicyID  -- D-244: usp_Load_DimPlatform adds each PolicyID
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceAccountId = src.SourceAccountId
    WHEN MATCHED THEN
        UPDATE SET AccountName = src.AccountName, Address = src.Address, UserName = src.UserName,
                   PlatformKey = src.PlatformKey, SafeKey = src.SafeKey,
                   LastCPMModifiedDate = src.LastCPMModifiedDate, LastVerifiedDate = src.LastVerifiedDate,
                   IsDeletedInSource = src.IsDeleted, PlatformLogonDomain = src.PlatformLogonDomain,
                   AutoManaged = src.AutoManaged, ManualReason = src.ManualReason
    WHEN NOT MATCHED BY TARGET AND src.IsDeleted = 0 THEN
        INSERT (SourceSystemKey, SourceAccountId, AccountName, Address, UserName, PlatformKey, SafeKey,
                CreatedDate, LastCPMModifiedDate, LastVerifiedDate, IsDeleted, PlatformLogonDomain, AutoManaged, ManualReason)
        VALUES (src.SourceSystemKey, src.SourceAccountId, src.AccountName, src.Address, src.UserName,
                src.PlatformKey, src.SafeKey, src.CreatedDate, src.LastCPMModifiedDate, src.LastVerifiedDate, 0,
                src.PlatformLogonDomain, src.AutoManaged, src.ManualReason)
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

IF OBJECT_ID('dbo.stg_discovered_accounts', 'U') IS NOT NULL DROP TABLE dbo.stg_discovered_accounts;
GO
