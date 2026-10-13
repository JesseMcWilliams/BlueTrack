/* ============================================================================
   12_BlueTrack_CyberArkSourceConfig.sql

   RUN THIS AFTER 01-11. Guarded -- safe to re-run.

   D-211 (Data Sources phase 3, Planning_Data-Sources.md): the CyberArk
   sources' settings move out of the nightly Agent job's command text and
   into a table edited on Admin > Data Sources.

   - web.cyberark_source_config: one row (key 1). Which sources are
     imported, the Privilege Cloud export folder and its seven file names,
     and the Self-Hosted EVD database. A file name may contain {format}
     tokens (e.g. {yyyy-MM-dd}), filled in with the run's date by
     dbo.fn_ExpandDateTokens. No row is created here: the installer's
     Db.NightlyJob step writes it from its answers when none exists, and
     the page can create it.
   - dbo.usp_Import_Configured: reads the row and calls usp_Import_All. The
     Agent job's Import step becomes EXEC usp_Import_Configured; (Database/
     Manual/04_BlueTrack_ScheduleImportLoadJob.sql). Until the job is
     recreated, the old step keeps working with the values written into it.
   ============================================================================ */

USE $DatabaseName$;
GO

IF OBJECT_ID('web.cyberark_source_config') IS NULL
BEGIN
    CREATE TABLE web.cyberark_source_config (
        CyberArkSourceConfigKey  INT            NOT NULL CONSTRAINT PK_cyberark_source_config PRIMARY KEY
                                                CONSTRAINT CK_cyberark_source_config_SingleRow CHECK (CyberArkSourceConfigKey = 1),
        ImportPrivilegeCloud     BIT            NOT NULL,
        ExportFolderPath         NVARCHAR(400)  NULL,
        PlatformsFileName        NVARCHAR(200)  NOT NULL CONSTRAINT DF_cyberark_source_config_Platforms    DEFAULT N'Export_PlatformsList.csv',
        UsersFileName            NVARCHAR(200)  NOT NULL CONSTRAINT DF_cyberark_source_config_Users        DEFAULT N'Export_UsersList.csv',
        GroupsFileName           NVARCHAR(200)  NOT NULL CONSTRAINT DF_cyberark_source_config_Groups       DEFAULT N'Export_GroupsList.csv',
        GroupMembersFileName     NVARCHAR(200)  NOT NULL CONSTRAINT DF_cyberark_source_config_GroupMembers DEFAULT N'Export Local Group Members {yyyy-MM-dd}.csv',
        SafesFileName            NVARCHAR(200)  NOT NULL CONSTRAINT DF_cyberark_source_config_Safes        DEFAULT N'Export_SafesList.csv',
        AccountsFileName         NVARCHAR(200)  NOT NULL CONSTRAINT DF_cyberark_source_config_Accounts     DEFAULT N'Export_AccountsList.csv',
        EntitlementsFileName     NVARCHAR(200)  NOT NULL CONSTRAINT DF_cyberark_source_config_Entitlements DEFAULT N'Export Entitlements {yyyy-MM-dd}.csv',
        ImportSelfHosted         BIT            NOT NULL,
        EvdDatabaseName          NVARCHAR(128)  NULL,
        ModifiedBy               INT            NULL CONSTRAINT FK_cyberark_source_config_ModifiedBy REFERENCES web.app_user(UserKey),
        ModifiedDate             DATETIME2      NOT NULL CONSTRAINT DF_cyberark_source_config_ModifiedDate DEFAULT SYSUTCDATETIME()
    );
END
GO

/* Each {format} token in @Pattern replaced by @Date in that .NET date
   format (FORMAT()), the same tokens a data feed's file name pattern takes.
   A token FORMAT can't use comes back as NULL, which makes the result NULL. */
CREATE OR ALTER FUNCTION dbo.fn_ExpandDateTokens (@Pattern NVARCHAR(200), @Date DATE)
RETURNS NVARCHAR(400)
AS
BEGIN
    DECLARE @Result NVARCHAR(400) = N'';
    DECLARE @Rest NVARCHAR(400) = @Pattern;
    DECLARE @Open INT, @Close INT;

    WHILE 1 = 1
    BEGIN
        SET @Open = CHARINDEX(N'{', @Rest);
        SET @Close = CASE WHEN @Open > 0 THEN CHARINDEX(N'}', @Rest, @Open + 1) ELSE 0 END;
        IF @Open = 0 OR @Close = 0 BREAK;
        SET @Result = @Result + LEFT(@Rest, @Open - 1)
                    + FORMAT(@Date, SUBSTRING(@Rest, @Open + 1, @Close - @Open - 1), 'en-US');
        SET @Rest = SUBSTRING(@Rest, @Close + 1, 400);
    END

    RETURN @Result + @Rest;
END
GO

CREATE OR ALTER PROCEDURE dbo.usp_Import_Configured
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @ImportPrivilegeCloud BIT, @ImportSelfHosted BIT, @Folder NVARCHAR(400), @EvdDatabaseName NVARCHAR(128),
            @Platforms NVARCHAR(200), @Users NVARCHAR(200), @Groups NVARCHAR(200), @GroupMembers NVARCHAR(200),
            @Safes NVARCHAR(200), @Accounts NVARCHAR(200), @Entitlements NVARCHAR(200);

    SELECT @ImportPrivilegeCloud = ImportPrivilegeCloud, @ImportSelfHosted = ImportSelfHosted,
           @Folder = ExportFolderPath, @EvdDatabaseName = EvdDatabaseName,
           @Platforms = PlatformsFileName, @Users = UsersFileName, @Groups = GroupsFileName,
           @GroupMembers = GroupMembersFileName, @Safes = SafesFileName, @Accounts = AccountsFileName,
           @Entitlements = EntitlementsFileName
    FROM web.cyberark_source_config
    WHERE CyberArkSourceConfigKey = 1;

    IF @@ROWCOUNT = 0
        THROW 50000, 'usp_Import_Configured: there are no CyberArk source settings. Set them on Admin > Data Sources, or re-run the installer''s Db.NightlyJob step.', 1;

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
        @ImportSelfHosted = @ImportSelfHosted;
END
GO
