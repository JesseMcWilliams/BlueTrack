/* ============================================================================
   38_BlueTrack_SelfHostedDirect.sql

   RUN THIS AFTER 01-37. Guarded -- safe to re-run.

   D-248 (Claude_Docs/Design_Data-Sources.md, "Self-Hosted Direct"): the
   Self-Hosted source can be read straight from PVWA's REST API instead of
   the EVD database. The API pulls PVWA into the same stg_sh_* tables the EVD
   import fills, so the Load is unchanged.

   - web.cyberark_source_config: SelfHostedSource ('Evd' or 'Direct'), the
     PVWA address, the named credential (web.credential, so CP or CCP), the
     logon method, when the nightly pull runs, and the largest change in
     Safe or account count a pull may make before it is rejected.
   - dbo.cyberark_direct_pull: one row per pull (counts, outcome, reason).
     The next pull compares its counts with the last Succeeded row.
   - dbo.stg_sh_platforms: PVWA's platform list (name, description, active).
     usp_Load_DimPlatform uses it when it holds rows.
   - dbo.usp_Import_Configured: with SelfHostedSource = 'Direct' the Import
     step no longer reads an EVD; it checks the latest pull succeeded and is
     less than 20 hours old, and fails the night otherwise.
   - dbo.usp_Import_All: new @SelfHostedStaged, so the Import leaves the
     pulled stg_sh_* tables alone instead of clearing them.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.cyberark_source_config', 'SelfHostedSource') IS NULL
    ALTER TABLE web.cyberark_source_config ADD SelfHostedSource NVARCHAR(10) NOT NULL
        CONSTRAINT DF_cyberark_source_config_SelfHostedSource DEFAULT N'Evd'
        CONSTRAINT CK_cyberark_source_config_SelfHostedSource CHECK (SelfHostedSource IN (N'Evd', N'Direct'));
GO

IF COL_LENGTH('web.cyberark_source_config', 'PvwaBaseUrl') IS NULL
    ALTER TABLE web.cyberark_source_config ADD PvwaBaseUrl NVARCHAR(400) NULL;
GO

IF COL_LENGTH('web.cyberark_source_config', 'PvwaCredentialKey') IS NULL
    ALTER TABLE web.cyberark_source_config ADD PvwaCredentialKey INT NULL
        CONSTRAINT FK_cyberark_source_config_PvwaCredential REFERENCES web.credential(CredentialKey);
GO

IF COL_LENGTH('web.cyberark_source_config', 'PvwaLogonMethod') IS NULL
    ALTER TABLE web.cyberark_source_config ADD PvwaLogonMethod NVARCHAR(20) NOT NULL
        CONSTRAINT DF_cyberark_source_config_PvwaLogonMethod DEFAULT N'CyberArk'
        CONSTRAINT CK_cyberark_source_config_PvwaLogonMethod CHECK (PvwaLogonMethod IN (N'CyberArk', N'LDAP'));
GO

IF COL_LENGTH('web.cyberark_source_config', 'DirectPullTime') IS NULL
    ALTER TABLE web.cyberark_source_config ADD DirectPullTime NVARCHAR(5) NOT NULL
        CONSTRAINT DF_cyberark_source_config_DirectPullTime DEFAULT N'01:30';
GO

IF COL_LENGTH('web.cyberark_source_config', 'DirectMaxChangePercent') IS NULL
    ALTER TABLE web.cyberark_source_config ADD DirectMaxChangePercent INT NOT NULL
        CONSTRAINT DF_cyberark_source_config_DirectMaxChangePercent DEFAULT 20
        CONSTRAINT CK_cyberark_source_config_DirectMaxChangePercent CHECK (DirectMaxChangePercent BETWEEN 0 AND 100);
GO

IF OBJECT_ID('dbo.cyberark_direct_pull') IS NULL
BEGIN
    CREATE TABLE dbo.cyberark_direct_pull (
        PullKey        BIGINT IDENTITY(1,1) CONSTRAINT PK_cyberark_direct_pull PRIMARY KEY,
        StartedAt      DATETIME2      NOT NULL CONSTRAINT DF_cyberark_direct_pull_StartedAt DEFAULT SYSUTCDATETIME(),
        CompletedAt    DATETIME2      NULL,
        Status         NVARCHAR(20)   NOT NULL CONSTRAINT DF_cyberark_direct_pull_Status DEFAULT N'Started'
                                      CONSTRAINT CK_cyberark_direct_pull_Status CHECK (Status IN (N'Started', N'Succeeded', N'Rejected', N'Failed')),
        TriggeredBy    NVARCHAR(100)  NOT NULL,   -- 'schedule' or the user's display name
        AcceptedChange BIT            NOT NULL CONSTRAINT DF_cyberark_direct_pull_AcceptedChange DEFAULT 0,   -- an admin accepted a change over the threshold
        ImportBatchId  UNIQUEIDENTIFIER NULL,
        SafeCount      INT            NULL,       -- Safes the service user can see
        AccountCount   INT            NULL,       -- accounts only: discovered (pending) accounts are a separate, clearable pool
        UserCount      INT            NULL,
        GroupCount     INT            NULL,
        MemberCount    INT            NULL,
        PlatformCount  INT            NULL,
        Message        NVARCHAR(2000) NULL        -- why a pull failed or was rejected, or warnings from a successful one
    );
    CREATE NONCLUSTERED INDEX IX_cyberark_direct_pull_StartedAt ON dbo.cyberark_direct_pull (StartedAt) INCLUDE (Status, CompletedAt);
END
GO

IF OBJECT_ID('dbo.stg_sh_platforms') IS NULL
BEGIN
    CREATE TABLE dbo.stg_sh_platforms (
        LoadId         BIGINT IDENTITY(1,1) PRIMARY KEY,
        ImportBatchId  UNIQUEIDENTIFIER NOT NULL,
        SourceFileName NVARCHAR(260)    NOT NULL,
        LoadTimestamp  DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        PlatformID     NVARCHAR(100)    NOT NULL,
        PlatformName   NVARCHAR(200)    NULL,
        Description    NVARCHAR(1000)   NULL,
        IsActive       BIT              NULL,
        PlatformType   NVARCHAR(50)     NULL
    );
END
GO

/* usp_Clear_Staging_SelfHosted: as in 03_BlueTrack_Baseline_SourceImport.sql, plus stg_sh_platforms. */
CREATE OR ALTER PROCEDURE usp_Clear_Staging_SelfHosted
AS
BEGIN
    SET NOCOUNT ON;
    TRUNCATE TABLE stg_sh_users;
    TRUNCATE TABLE stg_sh_groups;
    TRUNCATE TABLE stg_sh_groupmembers;
    TRUNCATE TABLE stg_sh_safes;
    TRUNCATE TABLE stg_sh_owners;
    TRUNCATE TABLE stg_sh_files;
    TRUNCATE TABLE stg_sh_objectproperties;
    TRUNCATE TABLE stg_sh_requests;
    TRUNCATE TABLE stg_sh_confirmations;
    TRUNCATE TABLE stg_sh_platforms;
END
GO

/* usp_Load_DimPlatform: as in 36_BlueTrack_SelfHostedPlatformsAndProperties.sql, plus the Self-Hosted Direct
   platform list. */
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

    -- ---- Self-Hosted Direct (D-248): PVWA's own platform list names and describes them ----
    -- stg_sh_platforms is empty unless a Direct pull filled it, so an EVD install is untouched.
    -- A platform no account uses is added too, since the list is the Vault's own.
    INSERT INTO dim_platform (SourceSystemKey, PlatformID, PlatformName)
    SELECT @SelfHostedKey, sp.PlatformID, ISNULL(NULLIF(sp.PlatformName, N''), sp.PlatformID)
    FROM stg_sh_platforms sp
    WHERE @SelfHostedKey IS NOT NULL
      AND NOT EXISTS (SELECT 1 FROM dim_platform p WHERE p.SourceSystemKey = @SelfHostedKey AND p.PlatformID = sp.PlatformID);

    UPDATE p
    SET PlatformName = ISNULL(NULLIF(sp.PlatformName, N''), p.PlatformName),
        Description = sp.Description, IsActive = sp.IsActive, PlatformType = sp.PlatformType
    FROM dim_platform p
    JOIN stg_sh_platforms sp ON sp.PlatformID = p.PlatformID
    WHERE p.SourceSystemKey = @SelfHostedKey;
END
GO

/* usp_Import_All: as in 03_BlueTrack_Baseline_SourceImport.sql, plus @SelfHostedStaged. */
CREATE OR ALTER PROCEDURE usp_Import_All (
    @EVDDatabaseName   NVARCHAR(128) = NULL,
    @PlatformsFile     NVARCHAR(500) = NULL,
    @UsersFile         NVARCHAR(500) = NULL,
    @GroupsFile        NVARCHAR(500) = NULL,
    @GroupMembersFile  NVARCHAR(500) = NULL,
    @SafesFile         NVARCHAR(500) = NULL,
    @AccountsFile      NVARCHAR(500) = NULL,
    @EntitlementsFile  NVARCHAR(500) = NULL,
    @EntitlementsExportDate DATE = NULL,
    @ImportPrivilegeCloud BIT = 1,
    @ImportSelfHosted     BIT = 1,
    @SelfHostedStaged     BIT = 0   -- D-248: Self-Hosted Direct filled the stg_sh_* tables already; neither import nor clear them
)
AS
BEGIN
    SET NOCOUNT ON;

    IF ISNULL(@ImportPrivilegeCloud, 0) = 0 AND ISNULL(@ImportSelfHosted, 0) = 0 AND ISNULL(@SelfHostedStaged, 0) = 0
        THROW 50000, 'usp_Import_All: both @ImportPrivilegeCloud and @ImportSelfHosted are off -- at least one source must be imported.', 1;

    -- Validate everything before importing anything, so a misconfigured
    -- run fails before it has emptied or half-filled any staging table.
    IF @ImportSelfHosted = 1
    BEGIN
        IF @EVDDatabaseName IS NULL OR LTRIM(RTRIM(@EVDDatabaseName)) = N''
            THROW 50000, 'usp_Import_All: @ImportSelfHosted = 1 but @EVDDatabaseName is not set.', 1;
        IF DB_ID(@EVDDatabaseName) IS NULL
        BEGIN
            DECLARE @NoEvdMessage NVARCHAR(400) = N'usp_Import_All: Self-Hosted EVD database ''' + @EVDDatabaseName
                + N''' was not found on this SQL Server instance. If this implementation has no Self-Hosted vault, import with @ImportSelfHosted = 0.';
            THROW 50000, @NoEvdMessage, 1;
        END
    END

    IF @ImportPrivilegeCloud = 1
    BEGIN
        DECLARE @MissingPc NVARCHAR(400) = STUFF(
              CASE WHEN @PlatformsFile IS NULL THEN N', @PlatformsFile' ELSE N'' END
            + CASE WHEN @UsersFile IS NULL THEN N', @UsersFile' ELSE N'' END
            + CASE WHEN @GroupsFile IS NULL THEN N', @GroupsFile' ELSE N'' END
            + CASE WHEN @GroupMembersFile IS NULL THEN N', @GroupMembersFile' ELSE N'' END
            + CASE WHEN @SafesFile IS NULL THEN N', @SafesFile' ELSE N'' END
            + CASE WHEN @AccountsFile IS NULL THEN N', @AccountsFile' ELSE N'' END
            + CASE WHEN @EntitlementsFile IS NULL THEN N', @EntitlementsFile' ELSE N'' END
            + CASE WHEN @EntitlementsExportDate IS NULL THEN N', @EntitlementsExportDate' ELSE N'' END, 1, 2, N'');
        IF @MissingPc IS NOT NULL
        BEGIN
            DECLARE @MissingPcMessage NVARCHAR(500) = N'usp_Import_All: @ImportPrivilegeCloud = 1 but these are not set: ' + @MissingPc
                + N'. If this implementation has no Privilege Cloud tenant, import with @ImportPrivilegeCloud = 0.';
            THROW 50000, @MissingPcMessage, 1;
        END
    END

    IF @ImportSelfHosted = 1
    BEGIN
        EXEC usp_Import_SelfHosted_EVD @EVDDatabaseName = @EVDDatabaseName;
        TRUNCATE TABLE stg_sh_platforms;   -- the EVD has no platform list; drop any left from a Direct pull
    END
    ELSE IF ISNULL(@SelfHostedStaged, 0) = 0
        EXEC usp_Clear_Staging_SelfHosted;

    IF @ImportPrivilegeCloud = 1
        EXEC usp_Import_PrivilegeCloud_All
            @PlatformsFile = @PlatformsFile, @UsersFile = @UsersFile, @GroupsFile = @GroupsFile,
            @GroupMembersFile = @GroupMembersFile, @SafesFile = @SafesFile, @AccountsFile = @AccountsFile,
            @EntitlementsFile = @EntitlementsFile, @EntitlementsExportDate = @EntitlementsExportDate;
    ELSE
        EXEC usp_Clear_Staging_PrivilegeCloud;
END
GO

/* usp_Import_Configured: as in 19_BlueTrack_MoreNotifications.sql, plus the Self-Hosted Direct gate. */
CREATE OR ALTER PROCEDURE dbo.usp_Import_Configured
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @EtlRunKey BIGINT;
    INSERT INTO dbo.etl_run_log (StepName) VALUES ('Import');
    SET @EtlRunKey = SCOPE_IDENTITY();

    BEGIN TRY
        DECLARE @SelfHostedSource NVARCHAR(10), @SelfHostedStaged BIT = 0;
        DECLARE @ImportPrivilegeCloud BIT, @ImportSelfHosted BIT, @Folder NVARCHAR(400), @EvdDatabaseName NVARCHAR(128),
                @Platforms NVARCHAR(200), @Users NVARCHAR(200), @Groups NVARCHAR(200), @GroupMembers NVARCHAR(200),
                @Safes NVARCHAR(200), @Accounts NVARCHAR(200), @Entitlements NVARCHAR(200);

        SELECT @ImportPrivilegeCloud = ImportPrivilegeCloud, @ImportSelfHosted = ImportSelfHosted,
               @Folder = ExportFolderPath, @EvdDatabaseName = EvdDatabaseName,
               @Platforms = PlatformsFileName, @Users = UsersFileName, @Groups = GroupsFileName,
               @GroupMembers = GroupMembersFileName, @Safes = SafesFileName, @Accounts = AccountsFileName,
               @Entitlements = EntitlementsFileName, @SelfHostedSource = SelfHostedSource
        FROM web.cyberark_source_config
        WHERE CyberArkSourceConfigKey = 1;

        IF @@ROWCOUNT = 0
            THROW 50000, 'usp_Import_Configured: there are no CyberArk source settings. Set them on Admin > Data Sources, or re-run the installer''s Db.NightlyJob step.', 1;

        /* D-248: with Self-Hosted Direct the API pulls PVWA into the stg_sh_* tables ahead of this job (at DirectPullTime).
           The Import only checks that the latest pull succeeded and is recent. A rejected or failed pull fails the night,
           so the Load doesn't run on stale or incomplete data and NightlyImportFailed mails the reason. */
        IF @ImportSelfHosted = 1 AND @SelfHostedSource = N'Direct'
        BEGIN
            DECLARE @PullStatus NVARCHAR(20), @PullCompleted DATETIME2, @PullMessage NVARCHAR(2000), @PullError NVARCHAR(2048);
            SELECT TOP (1) @PullStatus = Status, @PullCompleted = CompletedAt, @PullMessage = Message
            FROM dbo.cyberark_direct_pull ORDER BY PullKey DESC;

            IF @PullStatus IS NULL
                SET @PullError = N'Self-Hosted Direct: no PVWA pull has run yet. Run one from Admin > Data Sources, or check that the API is running.';
            ELSE IF @PullStatus = N'Started'
                SET @PullError = N'Self-Hosted Direct: the latest PVWA pull never finished.';
            ELSE IF @PullStatus IN (N'Failed', N'Rejected')
                SET @PullError = N'Self-Hosted Direct: the latest PVWA pull ' + LOWER(@PullStatus) + N'. ' + ISNULL(@PullMessage, N'');
            ELSE IF @PullCompleted < DATEADD(HOUR, -20, SYSUTCDATETIME())
                SET @PullError = N'Self-Hosted Direct: the latest successful PVWA pull is more than 20 hours old (' + CONVERT(NVARCHAR(30), @PullCompleted, 120) + N' UTC).';
            IF @PullError IS NOT NULL THROW 50000, @PullError, 1;

            SET @ImportSelfHosted = 0;
            SET @SelfHostedStaged = 1;
        END

        DECLARE @Today DATE = CAST(SYSDATETIME() AS DATE);
        DECLARE @PlatformsFile NVARCHAR(500), @UsersFile NVARCHAR(500), @GroupsFile NVARCHAR(500), @GroupMembersFile NVARCHAR(500),
                @SafesFile NVARCHAR(500), @AccountsFile NVARCHAR(500), @EntitlementsFile NVARCHAR(500);

        IF @ImportPrivilegeCloud = 1 AND NULLIF(LTRIM(RTRIM(@Folder)), N'') IS NOT NULL
        BEGIN
            IF RIGHT(@Folder, 1) <> N'\' SET @Folder = @Folder + N'\';
            -- usp_Import_All refuses a NULL path, so a bad {token} fails the run before anything is imported.
            SET @PlatformsFile    = @Folder + dbo.fn_ExpandDateTokens(@Platforms, @Today);
            SET @UsersFile        = @Folder + dbo.fn_ExpandDateTokens(@Users, @Today);
            SET @GroupsFile       = @Folder + dbo.fn_ExpandDateTokens(@Groups, @Today);
            SET @GroupMembersFile = @Folder + dbo.fn_ExpandDateTokens(@GroupMembers, @Today);
            SET @SafesFile        = @Folder + dbo.fn_ExpandDateTokens(@Safes, @Today);
            SET @AccountsFile     = @Folder + dbo.fn_ExpandDateTokens(@Accounts, @Today);
            SET @EntitlementsFile = @Folder + dbo.fn_ExpandDateTokens(@Entitlements, @Today);
        END

        EXEC usp_Import_All
            @EVDDatabaseName = @EvdDatabaseName,
            @PlatformsFile = @PlatformsFile,
            @UsersFile = @UsersFile,
            @GroupsFile = @GroupsFile,
            @GroupMembersFile = @GroupMembersFile,
            @SafesFile = @SafesFile,
            @AccountsFile = @AccountsFile,
            @EntitlementsFile = @EntitlementsFile,
            @EntitlementsExportDate = @Today,
            @ImportPrivilegeCloud = @ImportPrivilegeCloud,
            @ImportSelfHosted = @ImportSelfHosted,
            @SelfHostedStaged = @SelfHostedStaged;

        UPDATE dbo.etl_run_log SET Status = 'Succeeded', CompletedAt = SYSUTCDATETIME() WHERE EtlRunKey = @EtlRunKey;
    END TRY
    BEGIN CATCH
        -- A doomed transaction can't log; the import opens none of its own.
        IF XACT_STATE() = -1 ROLLBACK TRANSACTION;
        UPDATE dbo.etl_run_log
        SET Status = 'Failed', CompletedAt = SYSUTCDATETIME(), ErrorMessage = LEFT(ERROR_MESSAGE(), 2000),
            ErrorProcedure = ERROR_PROCEDURE(), ErrorLine = ERROR_LINE()
        WHERE EtlRunKey = @EtlRunKey;
        THROW;
    END CATCH
END
GO

PRINT '38_BlueTrack_SelfHostedDirect.sql complete.';
GO
