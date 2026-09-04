/* ============================================================================
   02_BlueTrack_ETL_LoadProcedures.sql

   RUN THIS FILE SECOND, after 01_BlueTrack_CreateDatabase_Schema.sql.
   Includes a USE $DatabaseName$; statement below, so you don't need to
   set the database context manually before running this.

   Staging -> Dimension/Fact Load Procedures
   Target: SQL Server / Azure SQL

   Contains the component load procedures, the Yes/No conversion helper
   function, and the CSV export view. The orchestration procedure that runs
   all of these in sequence (usp_RunFullLoad) is defined in
   04_BlueTrack_PowerBI_Support.sql instead of here, because it also
   needs to call usp_Load_AccountReconciliation (file 03) and
   usp_Load_FactAccountProgressHistory (file 04) -- putting it after
   everything it calls exists means no placeholder "add this step later"
   edits are needed once all four files have been run.

   DESIGN ASSUMPTION (confirm this matches your import process before running):
   Each staging table holds ONE full snapshot at a time -- i.e. your import
   job TRUNCATEs a staging table and reloads it completely from the latest
   CSV/EVD export, rather than appending every historical export on top of
   the last one. All MERGE logic below reads "the current contents of the
   staging table" as "the latest known state from that source." If your
   process instead accumulates every load into staging, filter every SELECT
   below to the latest ImportBatchId per source before merging, or these
   procs will merge stale, duplicate rows.

   Load order matters -- run in this sequence (usp_RunFullLoad, once
   defined in file 04, does this for you):
     1. dim_location
     2. dim_platform
     3. dim_safe
     4. dim_user
     5. dim_group
     6. bridge_group_membership (Privilege Cloud direct group members)
     7. fact_account
     8. fact_account_progress (creates Stage 1 rows for newly-loaded accounts)
     9. fact_safe_entitlement + bridge_entitlement_permission

   *** BATCHING NOTE ***
   CREATE FUNCTION, CREATE OR ALTER PROCEDURE, and CREATE OR ALTER VIEW must
   each be the only statement in their batch -- every one of them below is
   followed by GO for that reason. Do not remove those GO separators or
   merge these statements together.
   ============================================================================ */

USE $DatabaseName$;
GO

/* ----------------------------------------------------------------------------
   Helper: standardize the self-hosted Yes/No-style NVARCHAR(5) flags to BIT.
   CONFIRM the actual literal values in your data before trusting this --
   the DDL only tells us the column is NVARCHAR(5), not whether the vault
   writes 'Yes'/'No', 'Y'/'N', or something else. Run:
     SELECT DISTINCT CAUDisabled FROM stg_sh_users;
   and adjust the CASE below if the literals differ.
---------------------------------------------------------------------------- */
CREATE OR ALTER FUNCTION ufn_YNToBit (@val NVARCHAR(10))
RETURNS BIT
AS
BEGIN
    RETURN CASE
        WHEN UPPER(LTRIM(RTRIM(@val))) IN ('YES','Y','TRUE','1') THEN 1
        WHEN UPPER(LTRIM(RTRIM(@val))) IN ('NO','N','FALSE','0') THEN 0
        ELSE NULL
    END;
END
GO


/* ============================================================================
   1. dim_location  (Self-Hosted only -- Privilege Cloud exports carry no
      location hierarchy in the files you supplied)
   ============================================================================ */
CREATE OR ALTER PROCEDURE usp_Load_DimLocation
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');

    ;WITH SourceLocations AS (
        SELECT DISTINCT CAST(CAULocationID AS NVARCHAR(50)) AS SourceLocationId, CAULocationName AS LocationName
        FROM stg_sh_users WHERE CAULocationID IS NOT NULL
        UNION
        SELECT DISTINCT CAST(CAGLocationID AS NVARCHAR(50)), CAGLocationName
        FROM stg_sh_groups WHERE CAGLocationID IS NOT NULL
        UNION
        SELECT DISTINCT CAST(CASLocationID AS NVARCHAR(50)), CASLocationName
        FROM stg_sh_safes WHERE CASLocationID IS NOT NULL
    )
    MERGE dim_location AS tgt
    USING (SELECT @SelfHostedKey AS SourceSystemKey, SourceLocationId, LocationName FROM SourceLocations) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceLocationId = src.SourceLocationId
    WHEN MATCHED AND tgt.LocationName <> src.LocationName THEN
        UPDATE SET LocationName = src.LocationName
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, SourceLocationId, LocationName)
        VALUES (src.SourceSystemKey, src.SourceLocationId, src.LocationName);
END
GO


/* ============================================================================
   2. dim_platform  (Privilege Cloud only -- see schema notes: Self-Hosted's
      EVD export has no Platforms table)
   ============================================================================ */
CREATE OR ALTER PROCEDURE usp_Load_DimPlatform
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');

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
END
GO


/* ============================================================================
   3. dim_safe  (both sources)
   ============================================================================ */
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
        LEFT JOIN dim_location loc ON loc.SourceSystemKey = @SelfHostedKey
                                   AND loc.SourceLocationId = CAST(s.CASLocationID AS NVARCHAR(50))
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


/* ============================================================================
   4. dim_user  (both sources)
   ============================================================================ */
CREATE OR ALTER PROCEDURE usp_Load_DimUser
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
               CAST(NULL AS INT) AS LocationKey
        FROM stg_pc_users
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceUserId = src.SourceUserId
    WHEN MATCHED THEN
        UPDATE SET Username = src.Username, UserType = src.UserType, UserSource = src.UserSource,
                   ComponentUser = src.ComponentUser, Email = src.Email, FirstName = src.FirstName, LastName = src.LastName
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, SourceUserId, Username, UserType, UserSource, ComponentUser, Email, FirstName, LastName)
        VALUES (src.SourceSystemKey, src.SourceUserId, src.Username, src.UserType, src.UserSource,
                src.ComponentUser, src.Email, src.FirstName, src.LastName);

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
        LEFT JOIN dim_location loc ON loc.SourceSystemKey = @SelfHostedKey
                                   AND loc.SourceLocationId = CAST(u.CAULocationID AS NVARCHAR(50))
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


/* ============================================================================
   5. dim_group  (both sources)
   ============================================================================ */
CREATE OR ALTER PROCEDURE usp_Load_DimGroup
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');

    -- Privilege Cloud
    MERGE dim_group AS tgt
    USING (
        SELECT @PrivCloudKey AS SourceSystemKey, GroupID AS SourceGroupId, GroupName, Description,
               Location, GroupType, CAST(DirectoryType AS NVARCHAR(100)) AS DirectoryType,
               CAST(NULL AS INT) AS LocationKey
        FROM stg_pc_groups
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceGroupId = src.SourceGroupId
    WHEN MATCHED THEN
        UPDATE SET GroupName = src.GroupName, Description = src.Description, Location = src.Location,
                   GroupType = src.GroupType, DirectoryType = src.DirectoryType
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, SourceGroupId, GroupName, Description, Location, GroupType, DirectoryType)
        VALUES (src.SourceSystemKey, src.SourceGroupId, src.GroupName, src.Description, src.Location, src.GroupType, src.DirectoryType);

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
        LEFT JOIN dim_location loc ON loc.SourceSystemKey = @SelfHostedKey
                                   AND loc.SourceLocationId = CAST(g.CAGLocationID AS NVARCHAR(50))
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceGroupId = src.SourceGroupId
    WHEN MATCHED THEN
        UPDATE SET GroupName = src.GroupName, Description = src.Description, Location = src.Location,
                   GroupType = src.GroupType, DirectoryType = src.DirectoryType, LocationKey = src.LocationKey
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, SourceGroupId, GroupName, Description, Location, GroupType, DirectoryType, LocationKey)
        VALUES (src.SourceSystemKey, src.SourceGroupId, src.GroupName, src.Description, src.Location,
                src.GroupType, src.DirectoryType, src.LocationKey);
END
GO


/* ============================================================================
   6. fact_account  (both sources)

   Privilege Cloud: straight copy from stg_pc_accounts.
   Self-Hosted: CAFiles filtered to CAFType = 2 ('Password', per
   dim_selfhosted_code type 12) joined to the pivoted CAObjectProperties.

   *** PIVOT PROPERTY NAMES BELOW ARE PLACEHOLDERS ('UserName','Address',
   'PolicyID') *** -- confirm against your actual data first:
       SELECT DISTINCT CAOPObjectPropertyName FROM stg_sh_objectproperties;
   and edit the MAX(CASE WHEN ...) lines to match what you find.

   SourceAccountId for Self-Hosted is built as CAFSafeID + '_' + CAFFileID,
   confirmed to match the same pattern as Privilege Cloud's AccountID
   (e.g. '15_5'). This means an account that migrated from Self-Hosted to
   Privilege Cloud without changing its underlying Safe/File ID will produce
   the SAME SourceAccountId string in both stg_pc_accounts and the
   self-hosted pivot below -- they still land as two separate fact_account
   rows (one per SourceSystemKey) by design, since fact_account's natural
   key is (SourceSystemKey, SourceAccountId). If you want a single unified
   "this real-world account" record spanning its Self-Hosted history and its
   Privilege Cloud present, add a reconciliation table that links the two
   AccountKeys on a SourceAccountId match -- don't collapse them into one
   fact_account row, or you lose the ability to see which source is current.
   ============================================================================ */
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
                   IsDeleted = src.IsDeleted, PlatformLogonDomain = src.PlatformLogonDomain
    WHEN NOT MATCHED BY TARGET THEN
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
        UPDATE SET IsDeleted = 1;

    -- ---- Self-Hosted ----
    ;WITH PivotedAccounts AS (
        SELECT
            f.CAFSafeID, f.CAFFileID, f.CAFSafeName,
            CAST(f.CAFSafeID AS NVARCHAR(50)) + '_' + CAST(f.CAFFileID AS NVARCHAR(50)) AS SourceAccountId,
            f.CAFFileName AS AccountName,
            f.CAFCreationDate AS CreatedDate,
            f.CAFModificationDate AS LastCPMModifiedDate,
            f.CAFLastUsedDate AS LastVerifiedDate,
            -- PLACEHOLDER property names -- confirm against stg_sh_objectproperties before trusting
            MAX(CASE WHEN op.CAOPObjectPropertyName = 'UserName' THEN op.CAOPObjectPropertyValue END) AS UserName,
            MAX(CASE WHEN op.CAOPObjectPropertyName = 'Address'  THEN op.CAOPObjectPropertyValue END) AS Address,
            MAX(CASE WHEN op.CAOPObjectPropertyName = 'PolicyID' THEN op.CAOPObjectPropertyValue END) AS PolicyID
        FROM stg_sh_files f
        LEFT JOIN stg_sh_objectproperties op
               ON op.CAOPFileId = f.CAFFileID AND op.CAOPSafeId = f.CAFSafeID
        WHERE f.CAFType = 2   -- 'Password' per dim_selfhosted_code (CodeType 12, CodeValue 2)
        GROUP BY f.CAFSafeID, f.CAFFileID, f.CAFSafeName, f.CAFFileName,
                 f.CAFCreationDate, f.CAFModificationDate, f.CAFLastUsedDate
    )
    MERGE fact_account AS tgt
    USING (
        SELECT @SelfHostedKey AS SourceSystemKey, p.SourceAccountId, p.AccountName, p.Address, p.UserName,
               plat.PlatformKey, sf.SafeKey, p.CreatedDate, p.LastCPMModifiedDate, p.LastVerifiedDate
        FROM PivotedAccounts p
        LEFT JOIN dim_safe sf ON sf.SourceSystemKey = @SelfHostedKey AND sf.SafeUrlId = CAST(p.CAFSafeID AS NVARCHAR(300))
        LEFT JOIN dim_platform plat ON plat.SourceSystemKey = @SelfHostedKey AND plat.PlatformID = p.PolicyID  -- will stay NULL until a Self-Hosted platform reference exists (see schema notes)
    ) AS src
        ON tgt.SourceSystemKey = src.SourceSystemKey AND tgt.SourceAccountId = src.SourceAccountId
    WHEN MATCHED THEN
        UPDATE SET AccountName = src.AccountName, Address = src.Address, UserName = src.UserName,
                   PlatformKey = src.PlatformKey, SafeKey = src.SafeKey,
                   LastCPMModifiedDate = src.LastCPMModifiedDate, LastVerifiedDate = src.LastVerifiedDate,
                   IsDeleted = 0
    WHEN NOT MATCHED BY TARGET THEN
        INSERT (SourceSystemKey, SourceAccountId, AccountName, Address, UserName, PlatformKey, SafeKey,
                CreatedDate, LastCPMModifiedDate, LastVerifiedDate, IsDeleted)
        VALUES (src.SourceSystemKey, src.SourceAccountId, src.AccountName, src.Address, src.UserName,
                src.PlatformKey, src.SafeKey, src.CreatedDate, src.LastCPMModifiedDate, src.LastVerifiedDate, 0)
    WHEN NOT MATCHED BY SOURCE AND tgt.SourceSystemKey = @SelfHostedKey THEN
        UPDATE SET IsDeleted = 1;
END
GO


/* ============================================================================
   6b. fact_account_progress  (both sources) -- creates the Stage 1
   ("Discovered") tracking row for any account that doesn't have one yet.

   Never touches an existing progress row -- an account already being
   tracked keeps whatever stage/status/owner/etc. a person has since set,
   even if this proc runs again. This only handles brand-new accounts.

   AccountTypeKey and SORKey are pre-filled from platform_account_type_map
   as a suggested default based on the account's Platform, saving manual
   entry for the common case -- but both remain ordinary editable columns
   on fact_account_progress, so an analyst can override either one for an
   individual account that doesn't fit its Platform's usual pattern.
   Accounts on a Platform that hasn't been mapped yet in
   platform_account_type_map simply get NULL for both and are picked up
   once that mapping is completed (see vw_review_platform_sor_accounttype
   below to find unmapped platforms).
   ============================================================================ */
CREATE OR ALTER PROCEDURE usp_Load_FactAccountProgress
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @DiscoveredStageKey INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageName = 'Discovered');
    DECLARE @NotStartedStatusKey INT = (SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'Not Started');

    INSERT INTO fact_account_progress (AccountKey, CurrentStageKey, CurrentStatusKey, AccountTypeKey, SORKey)
    SELECT fa.AccountKey, @DiscoveredStageKey, @NotStartedStatusKey, pm.AccountTypeKey, pm.SORKey
    FROM fact_account fa
    LEFT JOIN platform_account_type_map pm ON pm.PlatformKey = fa.PlatformKey
    WHERE fa.IsDeleted = 0
      AND NOT EXISTS (SELECT 1 FROM fact_account_progress fap WHERE fap.AccountKey = fa.AccountKey);
END
GO


/* ============================================================================
   6b. Auto-advance to "Onboarded to Vault" (Stage 3)

   Business rule, direct from the user (2026-09-04): any account found in
   either source's data is considered onboarded to the vault unless it
   sits in a "pending accounts" safe -- matched by safe name containing
   "_Pending" (e.g. PasswordManager_Pending), not a single fixed name,
   since more than one such safe can exist. An account with no safe at
   all (SafeKey NULL) is NOT excluded by this rule (it isn't "in" a
   Pending safe), so it advances too.

   SAFEGUARD -- only ever touches a row still at its untouched default
   (CurrentStageKey = Discovered AND CurrentStatusKey = Not Started).
   Deliberately re-evaluated on every Load run (not just for brand-new
   accounts, unlike usp_Load_FactAccountProgress above) so the 2,432
   accounts already stuck at Discovered before this procedure existed get
   picked up retroactively -- but the same untouched-default check means
   it will never move an account a person has already started curating
   (changed its status, stage, owner, notes, etc.), which would otherwise
   silently fight Design_Data_Editing_Behavior.md's regression-requires-
   a-reason rule the next time this runs.

   NOT written to web.audit_event -- that table's PerformedByUserKey is
   NOT NULL, FK'd to web.app_user (a real logged-in person), and this
   runs from the ETL/Load pipeline with no such context. The daily
   usp_Load_FactAccountProgressHistory snapshot (run after this in
   usp_RunFullLoad) already gives a de facto record of exactly which day
   an account's StageKey changed, which is enough for a first cut; a
   dedicated system/service app_user row for ETL-originated audit events
   is a bigger decision left for later if it's actually needed.

   Stage 4 ("Managed / Rotation Enabled") is deliberately NOT handled
   here yet: it needs the safe to have a CPM assigned (dim_safe.ManagingCPM
   -- present in the schema and the source export, but empty for every
   safe in this test tenant's actual data) AND platform-level automatic-
   management/rotation settings, which the current Platforms export
   doesn't carry at all (only PlatformID/Name/Description/Active/
   PlatformType). Confirmed directly with the user (2026-09-04) to hold
   off on Stage 4 until a fuller Platform export is available, rather
   than build it against half the real rule.
   ============================================================================ */
CREATE OR ALTER PROCEDURE usp_Load_AccountProgressAutoAdvance
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @DiscoveredStageKey INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageName = 'Discovered');
    DECLARE @OnboardedStageKey INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageName = 'Onboarded to Vault');
    DECLARE @NotStartedStatusKey INT = (SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'Not Started');

    UPDATE fap
    SET fap.CurrentStageKey = @OnboardedStageKey,
        fap.LastUpdated = SYSUTCDATETIME()
    FROM fact_account_progress fap
    JOIN fact_account fa ON fa.AccountKey = fap.AccountKey
    LEFT JOIN dim_safe ds ON ds.SafeKey = fa.SafeKey
    WHERE fap.CurrentStageKey = @DiscoveredStageKey
      AND fap.CurrentStatusKey = @NotStartedStatusKey
      AND fa.IsDeleted = 0
      AND (ds.SafeKey IS NULL OR ds.SafeName NOT LIKE '%[_]Pending%');
END
GO


/* ============================================================================
   7. fact_safe_entitlement + bridge_entitlement_permission  (both sources)

   Uses CROSS APPLY (VALUES ...) to unpivot each source's wide boolean
   columns into the long/normalized bridge table, mapping raw column names
   to canonical permissions via permission_alias.

   NOTE ON APPROACH: earlier revisions of this proc tried to capture each
   newly-inserted EntitlementKey via OUTPUT ... INTO a table variable
   alongside staging columns like SafeUrlId/MemberId. That doesn't work --
   OUTPUT on an INSERT can only reference inserted./deleted. columns (the
   target table's own columns), never columns from the source SELECT, so
   SafeUrlId/MemberId/CAOSafeID/CAOOwnerID (which aren't columns on
   fact_safe_entitlement at all) can never appear there. Instead, each
   INSERT below is followed by a second statement that re-joins the same
   staging rows back to fact_safe_entitlement on the natural key
   (SafeKey, MemberType, UserKey, GroupKey, SnapshotDate) to find the
   EntitlementKey each row landed at. This assumes that natural key is
   unique per snapshot, which holds as long as the staging export itself
   has no duplicate (Safe, Member) rows -- true given the DESIGN
   ASSUMPTION at the top of this file that staging holds one clean current
   snapshot; a staging table with duplicate rows would fan out here.
   ============================================================================ */
CREATE OR ALTER PROCEDURE usp_Load_FactSafeEntitlement
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @SelfHostedKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');
    DECLARE @Today DATE = CAST(SYSUTCDATETIME() AS DATE);

    -- clear and reload each run -- entitlement exports are point-in-time
    -- snapshots, so this table is treated as "current state" rather than
    -- something to incrementally MERGE row-by-row like the dimensions above
    DELETE bep FROM bridge_entitlement_permission bep
        JOIN fact_safe_entitlement fse ON fse.EntitlementKey = bep.EntitlementKey
        JOIN dim_safe sf ON sf.SafeKey = fse.SafeKey
        WHERE sf.SourceSystemKey IN (@PrivCloudKey, @SelfHostedKey);
    DELETE fse FROM fact_safe_entitlement fse
        JOIN dim_safe sf ON sf.SafeKey = fse.SafeKey
        WHERE sf.SourceSystemKey IN (@PrivCloudKey, @SelfHostedKey);

    -- ---- Privilege Cloud entitlements ----
    INSERT INTO fact_safe_entitlement (SafeKey, MemberType, UserKey, GroupKey, MembershipExpirationDate,
                                        IsExpiredMembershipEnable, IsPredefinedUser, SnapshotDate)
    SELECT sf.SafeKey, e.MemberType, u.UserKey, g.GroupKey,
           e.MembershipExpirationDate, e.IsExpiredMembershipEnable, e.IsPredefinedUser, @Today
    FROM stg_pc_entitlements e
    JOIN dim_safe sf ON sf.SourceSystemKey = @PrivCloudKey AND sf.SafeUrlId = e.SafeUrlId
    LEFT JOIN dim_user  u ON e.MemberType = 'User'  AND u.SourceSystemKey = @PrivCloudKey AND u.SourceUserId  = e.MemberId
    LEFT JOIN dim_group g ON e.MemberType = 'Group' AND g.SourceSystemKey = @PrivCloudKey AND g.SourceGroupId = e.MemberId;

    INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted)
    SELECT fse.EntitlementKey, pa.PermissionKey, v.IsGranted
    FROM stg_pc_entitlements e
    JOIN dim_safe sf ON sf.SourceSystemKey = @PrivCloudKey AND sf.SafeUrlId = e.SafeUrlId
    LEFT JOIN dim_user  u ON e.MemberType = 'User'  AND u.SourceSystemKey = @PrivCloudKey AND u.SourceUserId  = e.MemberId
    LEFT JOIN dim_group g ON e.MemberType = 'Group' AND g.SourceSystemKey = @PrivCloudKey AND g.SourceGroupId = e.MemberId
    JOIN fact_safe_entitlement fse
        ON fse.SafeKey = sf.SafeKey
       AND fse.MemberType = e.MemberType
       AND ISNULL(fse.UserKey, -1)  = ISNULL(u.UserKey, -1)
       AND ISNULL(fse.GroupKey, -1) = ISNULL(g.GroupKey, -1)
       AND fse.SnapshotDate = @Today
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

    -- ---- Self-Hosted entitlements (CAOwners) ----
    -- CAOOwnerType decodes via dim_selfhosted_code type 10: 0=User, 1=Group, 2=Gateway account.
    -- Gateway-account owners aren't represented in dim_user or dim_group -- they're
    -- excluded below via the WHERE clause (rather than relying on the CHECK
    -- constraint to reject them). Revisit this if Gateway account owners
    -- matter for your reporting.
    INSERT INTO fact_safe_entitlement (SafeKey, MemberType, UserKey, GroupKey, MembershipExpirationDate,
                                        IsExpiredMembershipEnable, IsPredefinedUser, SnapshotDate)
    SELECT sf.SafeKey,
           CASE o.CAOOwnerType WHEN 0 THEN 'User' WHEN 1 THEN 'Group' ELSE NULL END,
           u.UserKey, g.GroupKey, o.CAOExpirationDate, NULL, NULL, @Today
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


/* ============================================================================
   8. bridge_group_membership  (Privilege Cloud only for now -- see table
   comment in the schema file for the Self-Hosted extension note)

   Confirmed against the real export: MemberID matches stg_pc_users.UserID
   for most rows, but built-in system users (Administrator, Backup,
   Auditor, Operator, DR, TelemetryUser, etc.) don't appear in the Users
   export and won't resolve here -- the INNER JOIN below silently excludes
   them rather than inserting a meaningless NULL UserKey row. That's
   expected, not a bug; uncomment the SELECT at the bottom of this proc to
   see exactly which members are being excluded on a given run.
   ============================================================================ */
CREATE OR ALTER PROCEDURE usp_Load_GroupMembership
AS
BEGIN
    SET NOCOUNT ON;
    DECLARE @PrivCloudKey INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
    DECLARE @Today DATE = CAST(SYSUTCDATETIME() AS DATE);

    DELETE bgm FROM bridge_group_membership bgm
        JOIN dim_group g ON g.GroupKey = bgm.GroupKey
        WHERE g.SourceSystemKey = @PrivCloudKey;

    INSERT INTO bridge_group_membership (GroupKey, UserKey, MemberLevel, SnapshotDate)
    SELECT DISTINCT g.GroupKey, u.UserKey, gm.MemberLevel, @Today
    FROM stg_pc_groupmembers gm
    JOIN dim_group g ON g.SourceSystemKey = @PrivCloudKey AND g.GroupName = gm.RootGroupName
    JOIN dim_user u  ON u.SourceSystemKey = @PrivCloudKey AND u.SourceUserId = gm.MemberID
    WHERE gm.MemberType = 'User';   -- only direct User members are handled; a future export containing Group-type nested members would need group-to-group expansion, not covered here

    -- Diagnostic: uncomment to see members that didn't resolve to a dim_user row
    -- SELECT DISTINCT gm.RootGroupName, gm.MemberName, gm.MemberID
    -- FROM stg_pc_groupmembers gm
    -- LEFT JOIN dim_user u ON u.SourceSystemKey = @PrivCloudKey AND u.SourceUserId = gm.MemberID
    -- WHERE gm.MemberType = 'User' AND u.UserKey IS NULL;
END
GO


/* ============================================================================
   REPORTING VIEW -- effective safe access, direct + group-expanded
   One row per (Safe, User) pair who can access it, whether granted
   directly or through a Group entitlement expanded via
   bridge_group_membership. A user with both direct and group-based access
   to the same safe intentionally appears twice (once per AccessPath) --
   that's informative, not a duplicate to dedupe away. This is the natural
   source for "how many actual known CyberArk users touch this safe/
   application" questions, e.g. for license-count estimation.
   ============================================================================ */
CREATE OR ALTER VIEW vw_effective_safe_access AS
SELECT fse.SafeKey, u.UserKey, 'Direct' AS AccessPath, fse.EntitlementKey AS SourceEntitlementKey
FROM fact_safe_entitlement fse
JOIN dim_user u ON u.UserKey = fse.UserKey
WHERE fse.MemberType = 'User'
UNION ALL
SELECT fse.SafeKey, bgm.UserKey, 'Via Group: ' + g.GroupName, fse.EntitlementKey
FROM fact_safe_entitlement fse
JOIN dim_group g ON g.GroupKey = fse.GroupKey
JOIN bridge_group_membership bgm ON bgm.GroupKey = g.GroupKey
WHERE fse.MemberType = 'Group';
GO


/* ============================================================================
   REPORTING VIEW -- CSV export source
   Point your CSV export (SSMS "Results to File", bcp, or an app-level
   export button) at this view rather than the raw fact table.
   ============================================================================ */
CREATE OR ALTER VIEW vw_export_account_progress AS
SELECT
    ss.SourceSystemName,
    fa.SourceAccountId,
    fa.AccountName,
    fa.UserName,
    fa.Address,
    dp.PlatformName,
    ds.SafeName,
    at.AccountTypeName,
    sor.SORName               AS SourceOfRecord,
    stg.StageName            AS CurrentStage,
    stat.StatusName          AS CurrentStatus,
    rl.RiskLevelName          AS RiskLevel,
    fap.OwnerName,
    fap.BusinessUnit,
    fap.TargetRemediationDate,
    fap.ActualCompletionDate,
    fap.LastUpdated,
    fap.Notes
FROM fact_account_progress fap
JOIN fact_account fa        ON fa.AccountKey = fap.AccountKey
JOIN dim_source_system ss   ON ss.SourceSystemKey = fa.SourceSystemKey
LEFT JOIN dim_platform dp   ON dp.PlatformKey = fa.PlatformKey
LEFT JOIN dim_safe ds       ON ds.SafeKey = fa.SafeKey
LEFT JOIN dim_account_type at ON at.AccountTypeKey = fap.AccountTypeKey
LEFT JOIN dim_source_of_record sor ON sor.SORKey = fap.SORKey
LEFT JOIN dim_risk_level rl ON rl.RiskLevelKey = fap.RiskLevelKey
JOIN dim_blueprint_stage stg ON stg.StageKey = fap.CurrentStageKey
JOIN dim_progress_status stat ON stat.StatusKey = fap.CurrentStatusKey;
GO


/* ============================================================================
   REPORTING VIEW -- platform mapping review queue
   Surfaces every Platform in use, whether it's been mapped in
   platform_account_type_map yet, and a sample Address from an account on
   that Platform as a manual sanity-check hint. Address is deliberately
   NOT used anywhere in the automated ETL logic -- it's too unreliable as a
   primary signal (a hostname doesn't reliably tell you what controls the
   identity logging into it) -- but it's a useful secondary confirmation
   for a human reviewer curating platform_account_type_map (e.g. an FQDN
   with a domain suffix supporting an 'Active Directory' classification).
   Point whoever maintains platform_account_type_map at this view; it does
   not write to any table.
   ============================================================================ */
CREATE OR ALTER VIEW vw_review_platform_sor_accounttype AS
SELECT
    dp.PlatformKey,
    ss.SourceSystemName,
    dp.PlatformID,
    dp.PlatformName,
    pm.AccountTypeKey,
    at.AccountTypeName,
    pm.SORKey,
    sor.SORName                AS SourceOfRecord,
    CASE WHEN pm.PlatformKey IS NULL THEN 1 ELSE 0 END AS IsUnmapped,
    accountCounts.AccountCount,
    sampleAccount.Address      AS SampleAddress,
    sampleAccount.AccountName  AS SampleAccountName
FROM dim_platform dp
JOIN dim_source_system ss ON ss.SourceSystemKey = dp.SourceSystemKey
LEFT JOIN platform_account_type_map pm ON pm.PlatformKey = dp.PlatformKey
LEFT JOIN dim_account_type at ON at.AccountTypeKey = pm.AccountTypeKey
LEFT JOIN dim_source_of_record sor ON sor.SORKey = pm.SORKey
OUTER APPLY (
    SELECT COUNT(*) AS AccountCount
    FROM fact_account fa
    WHERE fa.PlatformKey = dp.PlatformKey AND fa.IsDeleted = 0
) accountCounts
OUTER APPLY (
    SELECT TOP 1 fa.Address, fa.AccountName
    FROM fact_account fa
    WHERE fa.PlatformKey = dp.PlatformKey AND fa.IsDeleted = 0 AND fa.Address IS NOT NULL
    ORDER BY fa.AccountKey
) sampleAccount;
GO

PRINT 'ETL load procedures created successfully.';
