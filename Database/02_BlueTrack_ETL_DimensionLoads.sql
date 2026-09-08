/* ============================================================================
   02_BlueTrack_ETL_DimensionLoads.sql

   Split 2026-09-05 out of the former 02_BlueTrack_ETL_LoadProcedures.sql
   (see Database/README.md for the full script-numbering rationale). This
   file holds only the dimension-table loaders plus the one supporting
   helper function -- the fact-table loaders moved to
   03_BlueTrack_ETL_FactLoads.sql and the reporting views moved to
   04_BlueTrack_ETL_ReportingViews.sql. Object names, bodies, and logic are
   byte-for-byte unchanged from the original file; only the file layout
   changed.

   usp_Load_GroupMembership is deliberately included here rather than with
   the fact loaders it was originally grouped with -- usp_RunFullLoad (see
   06_BlueTrack_PowerBI_Support.sql) calls it immediately after
   usp_Load_DimGroup and before any fact-table procedure, so this ordering
   mirrors the real dependency/call order rather than the original file's
   layout.

   Depends on: 01_BlueTrack_CoreSchema.sql (all dim_* and stg_* tables
   referenced below must already exist).
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

PRINT '02_BlueTrack_ETL_DimensionLoads.sql complete.';
GO
