/* ============================================================================
   45_BlueTrack_SelfHostedDirectLoad.sql

   RUN THIS AFTER 01-44. Guarded -- safe to re-run.

   D-277 (Claude_Docs/Design_CyberArk-Direct-Integration.md, "What the Load
   reads"): phase 3 of moving a site off the EVD. When the Self-Hosted data
   staged tonight came from a Direct pull (dbo.fn_SelfHostedStagedByDirect),
   the Load also reads the REST-shaped stg_shd_* tables (script 41):

   - fact_account: AutoManaged (true and false), ManualReason, CPMStatus
     (trimmed, lowercased), SecretType, RemoteAccessRestricted,
     LastCPMModifiedDate (REST lastModifiedTime), LastReconciledDate,
     LastVerifiedDate (REST, else the latest verify in the CPM activity),
     and new columns CreatedTime, LastCPMAction, LastCPMActionResult,
     LastCPMActionTime. PlatformLogonDomain keeps its value where REST has
     none. Privilege Cloud's CPMStatus is lowercased too.
   - fact_safe_entitlement: from stg_shd_safe_members, every REST permission
     (permission_alias rows for the REST names), predefined members included,
     and a member with no user or group is kept as unresolved instead of
     failing the load (usp_Resolve_EntitlementMembers, as Privilege Cloud).
   - dim_group.DirectoryType from REST's directory.
   - Self-Hosted locations are keyed by their path (dim_location.
     SourceLocationId = LocationName) for both sources; existing rows keep
     their LocationKey.
   - dbo.vw_selfhosted_internal_safe: the Safes that are CyberArk internal
     ones (marked Internal on the shadow comparison, a built-in name, or a
     name that starts with a CPM's name), for the "internal" note on accounts
     deleted in CyberArk after the switch.

   With EVD-staged data every Self-Hosted step does exactly what it did
   before, so switching the source back to the EVD is the rollback.
   ============================================================================ */

USE $DatabaseName$;
GO

/* ---- 1. What is staged ------------------------------------------------------ */

/* 1 when tonight's Self-Hosted staging came from a Direct pull: a real pull writes stg_sh_* and stg_shd_* under one
   batch; an EVD import writes only stg_sh_*, and a shadow pull only stg_shd_* (so it never counts). */
CREATE OR ALTER FUNCTION dbo.fn_SelfHostedStagedByDirect()
RETURNS BIT
AS
BEGIN
    RETURN CASE WHEN EXISTS (
        SELECT 1 FROM dbo.stg_sh_safes s
        WHERE s.ImportBatchId IS NOT NULL
          AND EXISTS (SELECT 1 FROM dbo.stg_shd_safes d WHERE d.ImportBatchId = s.ImportBatchId)
    ) THEN 1 ELSE 0 END;
END
GO

/* ---- 2. New fact_account columns (Self-Hosted Direct) ----------------------- */

IF COL_LENGTH('dbo.fact_account', 'CreatedTime') IS NULL
    ALTER TABLE dbo.fact_account ADD CreatedTime DATETIME2(0) NULL;          -- REST createdTime (UTC)
GO
IF COL_LENGTH('dbo.fact_account', 'LastCPMAction') IS NULL
    ALTER TABLE dbo.fact_account ADD LastCPMAction NVARCHAR(20) NULL;        -- Verify, Change or Reconcile
GO
IF COL_LENGTH('dbo.fact_account', 'LastCPMActionResult') IS NULL
    ALTER TABLE dbo.fact_account ADD LastCPMActionResult NVARCHAR(20) NULL;  -- Success or Failure
GO
IF COL_LENGTH('dbo.fact_account', 'LastCPMActionTime') IS NULL
    ALTER TABLE dbo.fact_account ADD LastCPMActionTime DATETIME2(0) NULL;    -- UTC
GO

/* The counts a pull was compared with (D-277: the EVD's for the first Direct pull), for "40 to 10" on Data Sources. */
IF COL_LENGTH('dbo.cyberark_direct_pull', 'BaselineSafeCount') IS NULL
    ALTER TABLE dbo.cyberark_direct_pull ADD BaselineSafeCount INT NULL, BaselineAccountCount INT NULL;
GO

/* ---- 3. Self-Hosted locations keyed by path ---------------------------------- */

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('dbo.dim_location') AND name = 'SourceLocationId' AND max_length < 512)
BEGIN
    IF OBJECT_ID('dbo.UQ_dim_location', 'UQ') IS NOT NULL
        ALTER TABLE dbo.dim_location DROP CONSTRAINT UQ_dim_location;
    ALTER TABLE dbo.dim_location ALTER COLUMN SourceLocationId NVARCHAR(256) NOT NULL;
    ALTER TABLE dbo.dim_location ADD CONSTRAINT UQ_dim_location UNIQUE (SourceSystemKey, SourceLocationId);
END
GO

/* Each Self-Hosted location's key becomes its path; the first row of a name keeps its LocationKey, so nothing that
   points at it changes. A second row of the same name (a location renamed and re-added) keeps its old id and drops
   out of use at the next load. */
DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dbo.dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');
UPDATE l
SET SourceLocationId = l.LocationName
FROM dbo.dim_location l
WHERE l.SourceSystemKey = @SelfHostedKey
  AND l.SourceLocationId <> l.LocationName
  AND l.LocationKey = (SELECT MIN(x.LocationKey) FROM dbo.dim_location x WHERE x.SourceSystemKey = @SelfHostedKey AND x.LocationName = l.LocationName)
  AND NOT EXISTS (SELECT 1 FROM dbo.dim_location y WHERE y.SourceSystemKey = @SelfHostedKey AND y.SourceLocationId = l.LocationName);
GO

/* As in 02_BlueTrack_Baseline_EtlLoads.sql, keyed by the location's path (D-277) instead of the EVD's location id,
   which a Direct pull doesn't have. */
CREATE OR ALTER PROCEDURE usp_Load_DimLocation
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');

    ;WITH SourceLocations AS (
        SELECT CAULocationName AS LocationName FROM stg_sh_users WHERE NULLIF(CAULocationName, '') IS NOT NULL
        UNION
        SELECT CAGLocationName FROM stg_sh_groups WHERE NULLIF(CAGLocationName, '') IS NOT NULL
        UNION
        SELECT CASLocationName FROM stg_sh_safes WHERE NULLIF(CASLocationName, '') IS NOT NULL
    )
    MERGE dim_location AS tgt
    USING (SELECT @SelfHostedKey AS SourceSystemKey, LEFT(LocationName, 256) AS LocationName FROM SourceLocations) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceLocationId = src.LocationName
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, SourceLocationId, LocationName)
        VALUES (src.SourceSystemKey, src.LocationName, src.LocationName);
END
GO

/* ---- 4. Dimensions ------------------------------------------------------------ */

/* As in 02, with the Self-Hosted location joined by path (D-277). */
CREATE OR ALTER PROCEDURE usp_Load_DimSafe
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');

    -- Privilege Cloud
    MERGE dim_safe AS tgt
    USING (
        SELECT @PrivCloudKey AS SourceSystemKey, SafeUrlId, SafeName, SafeNumber, Description, Location,
               Creator AS CreatorUsername, OLACEnabled, CAST(ManagingCPM AS NVARCHAR(200)) AS ManagingCPM,
               VersionRetention, DayRetention, AutoPurge, Created AS CreatedDate, LastModified AS LastModifiedDate,
               CAST(NULL AS INT) AS LocationKey
        FROM stg_pc_safes
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SafeUrlId = src.SafeUrlId
    WHEN MATCHED THEN
        UPDATE SET SafeName = src.SafeName, SafeNumber = src.SafeNumber, Description = src.Description,
                   Location = src.Location, CreatorUsername = src.CreatorUsername, OLACEnabled = src.OLACEnabled,
                   ManagingCPM = src.ManagingCPM, VersionRetention = src.VersionRetention,
                   DayRetention = src.DayRetention, AutoPurge = src.AutoPurge,
                   CreatedDate = src.CreatedDate, LastModifiedDate = src.LastModifiedDate
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, SafeUrlId, SafeName, SafeNumber, Description, Location, CreatorUsername,
                OLACEnabled, ManagingCPM, VersionRetention, DayRetention, AutoPurge, CreatedDate, LastModifiedDate)
        VALUES (src.SourceSystemKey, src.SafeUrlId, src.SafeName, src.SafeNumber, src.Description, src.Location,
                src.CreatorUsername, src.OLACEnabled, src.ManagingCPM, src.VersionRetention, src.DayRetention,
                src.AutoPurge, src.CreatedDate, src.LastModifiedDate);

    -- Self-Hosted: no SafeUrlId in this source -- use CASSafeID (as string) as the natural key instead
    MERGE dim_safe AS tgt
    USING (
        SELECT @SelfHostedKey AS SourceSystemKey,
               CAST(s.CASSafeID AS NVARCHAR(300)) AS SafeUrlId,
               s.CASSafeName AS SafeName, CAST(s.CASSafeID AS INT) AS SafeNumber,
               CAST(NULL AS NVARCHAR(1000)) AS Description,
               s.CASLocationName AS Location, s.CASCreatedBy AS CreatorUsername,
               CAST(NULL AS BIT) AS OLACEnabled, CAST(NULL AS NVARCHAR(200)) AS ManagingCPM,
               s.CASYearlyVersions AS VersionRetention, s.CASObjectsRetentionPeriod AS DayRetention,
               CAST(NULL AS BIT) AS AutoPurge,
               CAST(s.CASCreationDate AS DATE) AS CreatedDate, CAST(NULL AS DATE) AS LastModifiedDate,
               loc.LocationKey
        FROM stg_sh_safes s
        LEFT JOIN dim_location loc ON loc.SourceSystemKey = @SelfHostedKey AND loc.SourceLocationId = s.CASLocationName
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SafeUrlId = src.SafeUrlId
    WHEN MATCHED THEN
        UPDATE SET SafeName = src.SafeName, Location = src.Location, CreatorUsername = src.CreatorUsername,
                   VersionRetention = src.VersionRetention, DayRetention = src.DayRetention,
                   CreatedDate = src.CreatedDate, LocationKey = src.LocationKey
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, SafeUrlId, SafeName, SafeNumber, Description, Location, CreatorUsername,
                OLACEnabled, ManagingCPM, VersionRetention, DayRetention, AutoPurge, CreatedDate, LastModifiedDate, LocationKey)
        VALUES (src.SourceSystemKey, src.SafeUrlId, src.SafeName, src.SafeNumber, src.Description, src.Location,
                src.CreatorUsername, src.OLACEnabled, src.ManagingCPM, src.VersionRetention, src.DayRetention,
                src.AutoPurge, src.CreatedDate, src.LastModifiedDate, src.LocationKey);
END
GO

/* As in 22_BlueTrack_PcImportByHeader.sql, with the Self-Hosted location joined by path (D-277). */
CREATE OR ALTER PROCEDURE dbo.usp_Load_DimUser
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');

    -- Privilege Cloud
    MERGE dim_user AS tgt
    USING (
        SELECT @PrivCloudKey AS SourceSystemKey, UserID AS SourceUserId, Username, UserType,
               Source AS UserSource, ComponentUser, Email, FirstName, LastName,
               Enabled, Suspended, Location,
               CAST(NULL AS INT) AS LocationKey
        FROM stg_pc_users
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceUserId = src.SourceUserId
    WHEN MATCHED THEN
        UPDATE SET Username = src.Username, UserType = src.UserType, UserSource = src.UserSource,
                   ComponentUser = src.ComponentUser, Email = src.Email, FirstName = src.FirstName, LastName = src.LastName,
                   Enabled = src.Enabled, Suspended = src.Suspended, Location = src.Location
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, SourceUserId, Username, UserType, UserSource, ComponentUser, Email, FirstName, LastName,
                Enabled, Suspended, Location)
        VALUES (src.SourceSystemKey, src.SourceUserId, src.Username, src.UserType, src.UserSource,
                src.ComponentUser, src.Email, src.FirstName, src.LastName,
                src.Enabled, src.Suspended, src.Location);

    -- Self-Hosted
    MERGE dim_user AS tgt
    USING (
        SELECT @SelfHostedKey AS SourceSystemKey, CAST(u.CAUUserID AS NVARCHAR(100)) AS SourceUserId,
               u.CAUUserName AS Username, CAST(u.CAUUserTypeID AS NVARCHAR(100)) AS UserType,
               CASE WHEN u.CAUExternalInternal = 1 THEN 'Internal'
                    WHEN u.CAUExternalInternal = 2 THEN 'External'
                    ELSE NULL END AS UserSource,          -- decoded via CATextCodes type 4
               CAST(NULL AS BIT) AS ComponentUser,
               u.CAUBusinessEmail AS Email, u.CAUFirstName AS FirstName, u.CAULastName AS LastName,
               loc.LocationKey
        FROM stg_sh_users u
        LEFT JOIN dim_location loc ON loc.SourceSystemKey = @SelfHostedKey AND loc.SourceLocationId = u.CAULocationName
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceUserId = src.SourceUserId
    WHEN MATCHED THEN
        UPDATE SET Username = src.Username, UserType = src.UserType, UserSource = src.UserSource,
                   Email = src.Email, FirstName = src.FirstName, LastName = src.LastName, LocationKey = src.LocationKey
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, SourceUserId, Username, UserType, UserSource, ComponentUser, Email, FirstName, LastName, LocationKey)
        VALUES (src.SourceSystemKey, src.SourceUserId, src.Username, src.UserType, src.UserSource,
                src.ComponentUser, src.Email, src.FirstName, src.LastName, src.LocationKey);
END
GO

/* As in 22, with the Self-Hosted location joined by path, and (D-277) DirectoryType from REST's directory when the
   staging came from a Direct pull (the EVD-shaped CAGLDAPDirectory is empty then). */
CREATE OR ALTER PROCEDURE dbo.usp_Load_DimGroup
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');

    -- Privilege Cloud
    MERGE dim_group AS tgt
    USING (
        SELECT @PrivCloudKey AS SourceSystemKey, GroupID AS SourceGroupId, GroupName, Description,
               Location, GroupType, Directory, CAST(DirectoryType AS NVARCHAR(100)) AS DirectoryType,
               CAST(NULL AS INT) AS LocationKey
        FROM stg_pc_groups
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceGroupId = src.SourceGroupId
    WHEN MATCHED THEN
        UPDATE SET GroupName = src.GroupName, Description = src.Description, Location = src.Location,
                   GroupType = src.GroupType, Directory = src.Directory, DirectoryType = src.DirectoryType
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, SourceGroupId, GroupName, Description, Location, GroupType, Directory, DirectoryType)
        VALUES (src.SourceSystemKey, src.SourceGroupId, src.GroupName, src.Description, src.Location, src.GroupType,
                src.Directory, src.DirectoryType);

    -- Self-Hosted
    MERGE dim_group AS tgt
    USING (
        SELECT @SelfHostedKey AS SourceSystemKey, CAST(g.CAGGroupID AS NVARCHAR(100)) AS SourceGroupId,
               g.CAGGroupName AS GroupName, g.CAGDescription AS Description, g.CAGLocationName AS Location,
               CASE WHEN g.CAGExternalInternal = 1 THEN 'Internal'
                    WHEN g.CAGExternalInternal = 2 THEN 'External'
                    ELSE NULL END AS GroupType,
               g.CAGLDAPDirectory AS DirectoryType,
               loc.LocationKey
        FROM stg_sh_groups g
        LEFT JOIN dim_location loc ON loc.SourceSystemKey = @SelfHostedKey AND loc.SourceLocationId = g.CAGLocationName
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceGroupId = src.SourceGroupId
    WHEN MATCHED THEN
        UPDATE SET GroupName = src.GroupName, Description = src.Description, Location = src.Location,
                   GroupType = src.GroupType, DirectoryType = src.DirectoryType, LocationKey = src.LocationKey
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, SourceGroupId, GroupName, Description, Location, GroupType, DirectoryType, LocationKey)
        VALUES (src.SourceSystemKey, src.SourceGroupId, src.GroupName, src.Description, src.Location,
                src.GroupType, src.DirectoryType, src.LocationKey);

    IF dbo.fn_SelfHostedStagedByDirect() = 1
        UPDATE g
        SET DirectoryType = LEFT(NULLIF(LTRIM(RTRIM(d.Directory)), ''), 100)
        FROM dim_group g
        JOIN stg_shd_groups d ON g.SourceSystemKey = @SelfHostedKey AND g.SourceGroupId = CAST(d.GroupId AS NVARCHAR(100));
END
GO

/* ---- 5. fact_account ----------------------------------------------------------- */

/* As in 36_BlueTrack_SelfHostedPlatformsAndProperties.sql, plus (D-277): Privilege Cloud's CPMStatus lowercased;
   with Direct staging, PlatformLogonDomain kept where REST has none, then the REST fields read from stg_shd_*. */
CREATE OR ALTER PROCEDURE usp_Load_FactAccount
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');
    DECLARE @Direct BIT = dbo.fn_SelfHostedStagedByDirect();
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
            a.SecretType, a.AutoManaged, LOWER(a.CPMStatus) AS CPMStatus, a.ManualReason,   -- D-277: lowercased, as Direct's
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
                   IsDeletedInSource = src.IsDeleted,
                   -- D-277: REST leaves LogonDomain out for some accounts; with Direct staging the last value is kept.
                   PlatformLogonDomain = CASE WHEN @Direct = 1 THEN COALESCE(src.PlatformLogonDomain, tgt.PlatformLogonDomain)
                                              ELSE src.PlatformLogonDomain END,
                   AutoManaged = src.AutoManaged, ManualReason = src.ManualReason
    WHEN NOT MATCHED BY TARGET AND src.IsDeleted = 0 THEN
        INSERT (SourceSystemKey, SourceAccountId, AccountName, Address, UserName, PlatformKey, SafeKey,
                CreatedDate, LastCPMModifiedDate, LastVerifiedDate, IsDeleted, PlatformLogonDomain, AutoManaged, ManualReason)
        VALUES (src.SourceSystemKey, src.SourceAccountId, src.AccountName, src.Address, src.UserName,
                src.PlatformKey, src.SafeKey, src.CreatedDate, src.LastCPMModifiedDate, src.LastVerifiedDate, 0,
                src.PlatformLogonDomain, src.AutoManaged, src.ManualReason)
    WHEN NOT MATCHED BY SOURCE AND tgt.SourceSystemKey = @SelfHostedKey THEN
        UPDATE SET IsDeletedInSource = 1;

    -- D-277: with Direct staging, the fields REST has and the EVD didn't, from stg_shd_accounts and the CPM activity
    -- (codes 22 Verify, 24 Change, 31 Reconcile succeeded; 38, 57, 60 the same failed). Times are UTC.
    IF @Direct = 1
        UPDATE fa
        SET AutoManaged = a.AutomaticManagementEnabled,
            ManualReason = LEFT(NULLIF(LTRIM(RTRIM(a.ManualManagementReason)), ''), 500),
            CPMStatus = NULLIF(LEFT(LOWER(LTRIM(RTRIM(a.CpmStatus))), 50), ''),
            SecretType = LEFT(NULLIF(LTRIM(RTRIM(a.SecretType)), ''), 50),
            RemoteAccessRestricted = a.AccessRestrictedToRemoteMachines,
            LastCPMModifiedDate = CAST(a.LastModifiedTime AS DATE),
            LastReconciledDate = CAST(COALESCE(a.LastReconciledTime, rec.ActivityTime) AS DATE),
            LastVerifiedDate = CAST(COALESCE(a.LastVerifiedTime, ver.ActivityTime) AS DATE),
            CreatedTime = a.CreatedTime,
            LastCPMAction = CASE lastAct.ActivityCode WHEN 22 THEN 'Verify' WHEN 38 THEN 'Verify' WHEN 24 THEN 'Change'
                                                      WHEN 57 THEN 'Change' WHEN 31 THEN 'Reconcile' WHEN 60 THEN 'Reconcile' END,
            LastCPMActionResult = CASE WHEN lastAct.ActivityCode IN (22, 24, 31) THEN 'Success'
                                       WHEN lastAct.ActivityCode IN (38, 57, 60) THEN 'Failure' END,
            LastCPMActionTime = lastAct.ActivityTime
        FROM fact_account fa
        JOIN stg_shd_accounts a ON fa.SourceSystemKey = @SelfHostedKey AND fa.SourceAccountId = a.AccountId
        OUTER APPLY (SELECT TOP (1) c.ActivityCode, c.ActivityTime FROM stg_shd_account_cpm_activity c
                     WHERE c.AccountId = a.AccountId AND c.ActivityCode IN (22, 24, 31, 38, 57, 60)
                     ORDER BY c.ActivityTime DESC) lastAct
        OUTER APPLY (SELECT MAX(c.ActivityTime) AS ActivityTime FROM stg_shd_account_cpm_activity c
                     WHERE c.AccountId = a.AccountId AND c.ActivityCode = 22) ver
        OUTER APPLY (SELECT MAX(c.ActivityTime) AS ActivityTime FROM stg_shd_account_cpm_activity c
                     WHERE c.AccountId = a.AccountId AND c.ActivityCode = 31) rec;

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

/* ---- 6. Entitlements ------------------------------------------------------------ */

/* D-277: the REST permission names for Self-Hosted. REST has one MoveAccountsAndFolders flag where the EVD has
   MoveFrom and MoveInto; the load emits it under both, so the permissions stay the ones Self-Hosted had. */
INSERT INTO dbo.permission_alias (SourceSystemKey, RawPermissionName, PermissionKey, Confidence)
SELECT ss.SourceSystemKey, v.RawName, dp.PermissionKey, 'confirmed'
FROM dbo.dim_source_system ss
CROSS JOIN (VALUES
    ('UseAccounts', 'UseAccounts'), ('RetrieveAccounts', 'RetrieveAccounts'), ('ListAccounts', 'ListAccounts'),
    ('AddAccounts', 'AddAccounts'), ('UpdateAccountContent', 'UpdateAccountContent'),
    ('UpdateAccountProperties', 'UpdateAccountProperties'),
    ('InitiateCPMAccountManagementOperations', 'InitiateCPMAccountManagementOperations'),
    ('SpecifyNextAccountContent', 'SpecifyNextAccountContent'), ('RenameAccounts', 'RenameAccounts'),
    ('DeleteAccounts', 'DeleteAccounts'), ('UnlockAccounts', 'UnlockAccounts'), ('ManageSafe', 'ManageSafe'),
    ('ManageSafeMembers', 'ManageSafeMembers'), ('BackupSafe', 'BackupSafe'), ('ViewAuditLog', 'ViewAuditLog'),
    ('ViewSafeMembers', 'ViewSafeMembers'), ('AccessWithoutConfirmation', 'AccessWithoutConfirmation'),
    ('CreateFolders', 'CreateFolders'), ('DeleteFolders', 'DeleteFolders'),
    ('MoveAccountsFrom', 'MoveAccountsFrom'), ('MoveAccountsInto', 'MoveAccountsInto'),
    ('RequestsAuthorizationLevel1', 'RequestsAuthorizationLevel1'), ('RequestsAuthorizationLevel2', 'RequestsAuthorizationLevel2')
) AS v(RawName, PermissionName)
JOIN dbo.dim_permission dp ON dp.PermissionName = v.PermissionName
WHERE ss.SourceSystemCode = 'SELFHOSTED'
  AND NOT EXISTS (SELECT 1 FROM dbo.permission_alias pa WHERE pa.SourceSystemKey = ss.SourceSystemKey AND pa.RawPermissionName = v.RawName);
GO

/* As in 24_BlueTrack_EntitlementMemberResolution.sql, plus (D-277): with Direct staging the Self-Hosted rows come from
   stg_shd_safe_members, resolved like Privilege Cloud's (manual, ID, name), so a member with no user or group is kept
   as unresolved instead of failing the load, and predefined members are included. EVD staging: unchanged. */
CREATE OR ALTER PROCEDURE dbo.usp_Load_FactSafeEntitlement
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');
    DECLARE @Today DATE = CAST(SYSUTCDATETIME() AS DATE);

    -- clear and reload each run -- entitlement exports are point-in-time snapshots
    DELETE bep FROM bridge_entitlement_permission bep
        JOIN fact_safe_entitlement fse ON fse.EntitlementKey = bep.EntitlementKey
        JOIN dim_safe sf ON sf.SafeKey = fse.SafeKey
        WHERE sf.SourceSystemKey IN (@PrivCloudKey, @SelfHostedKey);
    DELETE fse FROM fact_safe_entitlement fse
        JOIN dim_safe sf ON sf.SafeKey = fse.SafeKey
        WHERE sf.SourceSystemKey IN (@PrivCloudKey, @SelfHostedKey);

    -- ---- Privilege Cloud: resolve each distinct member once ----
    CREATE TABLE #entitlement_member (
        MemberType       NVARCHAR(20)  COLLATE DATABASE_DEFAULT NOT NULL,
        MemberId         NVARCHAR(300) COLLATE DATABASE_DEFAULT NOT NULL,
        MemberName       NVARCHAR(300) COLLATE DATABASE_DEFAULT NULL,
        UserKey          INT           NULL,
        GroupKey         INT           NULL,
        ResolutionMethod NVARCHAR(20)  COLLATE DATABASE_DEFAULT NULL,
        PRIMARY KEY (MemberType, MemberId)
    );

    INSERT INTO #entitlement_member (MemberType, MemberId, MemberName)
    SELECT e.MemberType, e.MemberId, MAX(e.MemberName)
    FROM stg_pc_entitlements e
    JOIN dim_safe sf ON sf.SourceSystemKey = @PrivCloudKey AND sf.SafeUrlId = e.SafeUrlId
    WHERE e.MemberType IN ('User', 'Group')
    GROUP BY e.MemberType, e.MemberId;

    EXEC dbo.usp_Resolve_EntitlementMembers @SourceSystemKey = @PrivCloudKey;

    CREATE TABLE #entitlement_row (EntitlementKey BIGINT NOT NULL PRIMARY KEY, LoadId BIGINT NOT NULL);

    MERGE fact_safe_entitlement AS tgt
    USING (
        SELECT e.LoadId, sf.SafeKey, e.MemberType, m.UserKey, m.GroupKey,
               CASE WHEN m.ResolutionMethod = 'ById' THEN NULL ELSE e.MemberId END AS UnresolvedMemberId,
               e.MemberName, m.ResolutionMethod,
               e.MembershipExpirationDate, e.IsExpiredMembershipEnable, e.IsPredefinedUser
        FROM stg_pc_entitlements e
        JOIN dim_safe sf ON sf.SourceSystemKey = @PrivCloudKey AND sf.SafeUrlId = e.SafeUrlId
        JOIN #entitlement_member m ON m.MemberType = e.MemberType AND m.MemberId = e.MemberId
    ) AS src
    ON 1 = 0
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SafeKey, MemberType, UserKey, GroupKey, UnresolvedMemberId, MemberName, ResolutionMethod,
                MembershipExpirationDate, IsExpiredMembershipEnable, IsPredefinedUser, SnapshotDate)
        VALUES (src.SafeKey, src.MemberType, src.UserKey, src.GroupKey, src.UnresolvedMemberId, src.MemberName, src.ResolutionMethod,
                src.MembershipExpirationDate, src.IsExpiredMembershipEnable, src.IsPredefinedUser, @Today)
    OUTPUT inserted.EntitlementKey, src.LoadId INTO #entitlement_row (EntitlementKey, LoadId);

    INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted)
    SELECT r.EntitlementKey, pa.PermissionKey, v.IsGranted
    FROM #entitlement_row r
    JOIN stg_pc_entitlements e ON e.LoadId = r.LoadId
    CROSS APPLY (VALUES
        ('UseAccounts', e.UseAccounts), ('RetrieveAccounts', e.RetrieveAccounts), ('ListAccounts', e.ListAccounts),
        ('AddAccounts', e.AddAccounts), ('UpdateAccountContent', e.UpdateAccountContent),
        ('UpdateAccountProperties', e.UpdateAccountProperties),
        ('InitiateCPMAccountManagementOperations', e.InitiateCPMAccountManagementOperations),
        ('SpecifyNextAccountContent', e.SpecifyNextAccountContent), ('RenameAccounts', e.RenameAccounts),
        ('DeleteAccounts', e.DeleteAccounts), ('UnlockAccounts', e.UnlockAccounts), ('ManageSafe', e.ManageSafe),
        ('ManageSafeMembers', e.ManageSafeMembers), ('BackupSafe', e.BackupSafe), ('ViewAuditLog', e.ViewAuditLog),
        ('ViewSafeMembers', e.ViewSafeMembers), ('AccessWithoutConfirmation', e.AccessWithoutConfirmation),
        ('CreateFolders', e.CreateFolders), ('DeleteFolders', e.DeleteFolders),
        ('MoveAccountsAndFolders', e.MoveAccountsAndFolders),
        ('RequestsAuthorizationLevel1', e.RequestsAuthorizationLevel1),
        ('RequestsAuthorizationLevel2', e.RequestsAuthorizationLevel2)
    ) AS v(RawPermissionName, IsGranted)
    JOIN permission_alias pa ON pa.SourceSystemKey = @PrivCloudKey AND pa.RawPermissionName = v.RawPermissionName
    WHERE v.IsGranted IS NOT NULL;

    IF dbo.fn_SelfHostedStagedByDirect() = 1
    BEGIN
        -- ---- Self-Hosted from a Direct pull (D-277): stg_shd_safe_members, resolved like Privilege Cloud ----
        TRUNCATE TABLE #entitlement_member;

        INSERT INTO #entitlement_member (MemberType, MemberId, MemberName)
        SELECT m.MemberType, CAST(m.MemberId AS NVARCHAR(300)), MAX(m.MemberName)
        FROM stg_shd_safe_members m
        JOIN dim_safe sf ON sf.SourceSystemKey = @SelfHostedKey AND sf.SafeUrlId = CAST(m.SafeNumber AS NVARCHAR(300))
        WHERE m.MemberType IN ('User', 'Group') AND m.MemberId IS NOT NULL
        GROUP BY m.MemberType, m.MemberId;

        EXEC dbo.usp_Resolve_EntitlementMembers @SourceSystemKey = @SelfHostedKey;

        CREATE TABLE #sh_entitlement_row (EntitlementKey BIGINT NOT NULL PRIMARY KEY, LoadId BIGINT NOT NULL);

        MERGE fact_safe_entitlement AS tgt
        USING (
            SELECT d.LoadId, sf.SafeKey, d.MemberType, m.UserKey, m.GroupKey,
                   CASE WHEN m.ResolutionMethod = 'ById' THEN NULL ELSE m.MemberId END AS UnresolvedMemberId,
                   d.MemberName, m.ResolutionMethod,
                   d.MembershipExpirationDate, d.IsExpiredMembershipEnable, d.IsPredefinedUser
            FROM stg_shd_safe_members d
            JOIN dim_safe sf ON sf.SourceSystemKey = @SelfHostedKey AND sf.SafeUrlId = CAST(d.SafeNumber AS NVARCHAR(300))
            JOIN #entitlement_member m ON m.MemberType = d.MemberType AND m.MemberId = CAST(d.MemberId AS NVARCHAR(300))
        ) AS src
        ON 1 = 0
        WHEN NOT MATCHED BY TARGET THEN
            INSERT (SafeKey, MemberType, UserKey, GroupKey, UnresolvedMemberId, MemberName, ResolutionMethod,
                    MembershipExpirationDate, IsExpiredMembershipEnable, IsPredefinedUser, SnapshotDate)
            VALUES (src.SafeKey, src.MemberType, src.UserKey, src.GroupKey, src.UnresolvedMemberId, src.MemberName, src.ResolutionMethod,
                    src.MembershipExpirationDate, src.IsExpiredMembershipEnable, src.IsPredefinedUser, @Today)
        OUTPUT inserted.EntitlementKey, src.LoadId INTO #sh_entitlement_row (EntitlementKey, LoadId);

        INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted)
        SELECT r.EntitlementKey, pa.PermissionKey, v.IsGranted
        FROM #sh_entitlement_row r
        JOIN stg_shd_safe_members d ON d.LoadId = r.LoadId
        CROSS APPLY (VALUES
            ('UseAccounts', d.UseAccounts), ('RetrieveAccounts', d.RetrieveAccounts), ('ListAccounts', d.ListAccounts),
            ('AddAccounts', d.AddAccounts), ('UpdateAccountContent', d.UpdateAccountContent),
            ('UpdateAccountProperties', d.UpdateAccountProperties),
            ('InitiateCPMAccountManagementOperations', d.InitiateCPMAccountManagementOperations),
            ('SpecifyNextAccountContent', d.SpecifyNextAccountContent), ('RenameAccounts', d.RenameAccounts),
            ('DeleteAccounts', d.DeleteAccounts), ('UnlockAccounts', d.UnlockAccounts), ('ManageSafe', d.ManageSafe),
            ('ManageSafeMembers', d.ManageSafeMembers), ('BackupSafe', d.BackupSafe), ('ViewAuditLog', d.ViewAuditLog),
            ('ViewSafeMembers', d.ViewSafeMembers), ('AccessWithoutConfirmation', d.AccessWithoutConfirmation),
            ('CreateFolders', d.CreateFolders), ('DeleteFolders', d.DeleteFolders),
            ('MoveAccountsFrom', d.MoveAccountsAndFolders), ('MoveAccountsInto', d.MoveAccountsAndFolders),
            ('RequestsAuthorizationLevel1', d.RequestsAuthorizationLevel1),
            ('RequestsAuthorizationLevel2', d.RequestsAuthorizationLevel2)
        ) AS v(RawPermissionName, IsGranted)
        JOIN permission_alias pa ON pa.SourceSystemKey = @SelfHostedKey AND pa.RawPermissionName = v.RawPermissionName
        WHERE v.IsGranted IS NOT NULL;
        RETURN;
    END

    -- ---- Self-Hosted entitlements (CAOwners), as before ----
    -- CAOOwnerType decodes via dim_selfhosted_code type 10: 0=User, 1=Group, 2=Gateway account.
    -- Gateway-account owners aren't represented in dim_user or dim_group and are excluded.
    INSERT INTO fact_safe_entitlement (SafeKey, MemberType, UserKey, GroupKey, ResolutionMethod, MembershipExpirationDate,
                                        IsExpiredMembershipEnable, IsPredefinedUser, SnapshotDate)
    SELECT sf.SafeKey,
           CASE o.CAOOwnerType WHEN 0 THEN 'User' WHEN 1 THEN 'Group' ELSE NULL END,
           u.UserKey, g.GroupKey, 'ById', o.CAOExpirationDate, NULL, NULL, @Today
    FROM stg_sh_owners o
    JOIN dim_safe sf ON sf.SourceSystemKey = @SelfHostedKey AND sf.SafeUrlId = CAST(o.CAOSafeID AS NVARCHAR(300))
    LEFT JOIN dim_user  u ON o.CAOOwnerType = 0 AND u.SourceSystemKey = @SelfHostedKey AND u.SourceUserId  = CAST(o.CAOOwnerID AS NVARCHAR(100))
    LEFT JOIN dim_group g ON o.CAOOwnerType = 1 AND g.SourceSystemKey = @SelfHostedKey AND g.SourceGroupId = CAST(o.CAOOwnerID AS NVARCHAR(100))
    WHERE o.CAOOwnerType IN (0, 1);

    INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted)
    SELECT fse.EntitlementKey, pa.PermissionKey, dbo.ufn_YNToBit(v.RawValue)
    FROM stg_sh_owners o
    JOIN dim_safe sf ON sf.SourceSystemKey = @SelfHostedKey AND sf.SafeUrlId = CAST(o.CAOSafeID AS NVARCHAR(300))
    LEFT JOIN dim_user  u ON o.CAOOwnerType = 0 AND u.SourceSystemKey = @SelfHostedKey AND u.SourceUserId  = CAST(o.CAOOwnerID AS NVARCHAR(100))
    LEFT JOIN dim_group g ON o.CAOOwnerType = 1 AND g.SourceSystemKey = @SelfHostedKey AND g.SourceGroupId = CAST(o.CAOOwnerID AS NVARCHAR(100))
    JOIN fact_safe_entitlement fse
        ON fse.SafeKey = sf.SafeKey
       AND fse.MemberType = CASE o.CAOOwnerType WHEN 0 THEN 'User' WHEN 1 THEN 'Group' ELSE NULL END
       AND ISNULL(fse.UserKey, -1)  = ISNULL(u.UserKey, -1)
       AND ISNULL(fse.GroupKey, -1) = ISNULL(g.GroupKey, -1)
       AND fse.SnapshotDate = @Today
    CROSS APPLY (VALUES
        ('CAOList', o.CAOList), ('CAORetrieve', o.CAORetrieve), ('CAOCreateObject', o.CAOCreateObject),
        ('CAOUpdateObject', o.CAOUpdateObject), ('CAOUpdateObjectProperties', o.CAOUpdateObjectProperties),
        ('CAORenameObject', o.CAORenameObject), ('CAODelete', o.CAODelete),
        ('CAOInitiateCPMChange', o.CAOInitiateCPMChange),
        ('CAOInitiateCPMChangeWithManualPassword', o.CAOInitiateCPMChangeWithManualPassword),
        ('CAOCreateFolder', o.CAOCreateFolder), ('CAODeleteFolder', o.CAODeleteFolder),
        ('CAOUnlockObject', o.CAOUnlockObject), ('CAOMoveFrom', o.CAOMoveFrom), ('CAOMoveInto', o.CAOMoveInto),
        ('CAOManageSafe', o.CAOManageSafe), ('CAOManageSafeOwners', o.CAOManageSafeOwners),
        ('CAOBackup', o.CAOBackup), ('CAONoConfirmRequired', o.CAONoConfirmRequired),
        ('CAOEventsList', o.CAOEventsList)
    ) AS v(RawPermissionName, RawValue)
    JOIN permission_alias pa ON pa.SourceSystemKey = @SelfHostedKey AND pa.RawPermissionName = v.RawPermissionName
    WHERE o.CAOOwnerType IN (0, 1) AND dbo.ufn_YNToBit(v.RawValue) IS NOT NULL;
END
GO

/* ---- 7. Internal Safes ------------------------------------------------------------ */

/* D-277: the Self-Hosted Safes that are CyberArk internal ones, so an account in one that is deleted in CyberArk after
   the switch to Direct (PVWA never returns them) can say why. The same rules as the shadow comparison's
   (43_BlueTrack_ShadowComparisonRules.sql; keep the name list in step): marked Internal on the comparison, a built-in
   internal name, or a name that starts with a CPM's name (a user of type 31, CPM). */
CREATE OR ALTER VIEW dbo.vw_selfhosted_internal_safe
AS
SELECT s.SafeKey,
       CASE WHEN mk.Category = N'Internal' THEN N'Marked'
            WHEN s.SafeName IN (N'AppProviderCacheSafe', N'AppProviderConf', N'PasswordManagerShared', N'PasswordManagerTemp', N'Pictures',
                                N'PSMLiveSessions', N'PSMNotifications', N'PSMSessions', N'PSMUnmanagedSessionAccounts',
                                N'PVWAConfig', N'PVWAPrivateUserPrefs', N'PVWATaskDefinitions', N'PVWAUserPrefs', N'System') THEN N'BuiltIn'
            ELSE N'CpmSafe' END AS InternalReason
FROM dbo.dim_safe s
JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = s.SourceSystemKey AND ss.SourceSystemCode = 'SELFHOSTED'
LEFT JOIN web.shd_comparison_mark mk ON mk.ItemKind = N'Safe' AND mk.ItemKey = s.SafeUrlId
WHERE (mk.Category = N'Internal')
   OR (mk.Category IS NULL
       AND (s.SafeName IN (N'AppProviderCacheSafe', N'AppProviderConf', N'PasswordManagerShared', N'PasswordManagerTemp', N'Pictures',
                           N'PSMLiveSessions', N'PSMNotifications', N'PSMSessions', N'PSMUnmanagedSessionAccounts',
                           N'PVWAConfig', N'PVWAPrivateUserPrefs', N'PVWATaskDefinitions', N'PVWAUserPrefs', N'System')
            OR EXISTS (SELECT 1 FROM dbo.dim_user u
                       WHERE u.SourceSystemKey = s.SourceSystemKey AND u.UserType = N'31'
                         AND LEN(u.Username) > 0 AND LEFT(s.SafeName, LEN(u.Username)) = u.Username)));
GO

PRINT '45_BlueTrack_SelfHostedDirectLoad.sql complete.';
GO
