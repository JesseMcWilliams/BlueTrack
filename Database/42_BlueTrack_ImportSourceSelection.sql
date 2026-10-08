/* ============================================================================
   42_BlueTrack_ImportSourceSelection.sql

   RUN THIS AFTER 01-41. Safe to re-run (CREATE OR ALTER -- pure logic, no
   data of its own).

   D-176: usp_Import_All (07_BlueTrack_SourceImport.sql) always imported BOTH
   sources -- the Self-Hosted EVD database and all seven Privilege Cloud
   CSV exports -- so on an implementation with only one of them, the
   nightly Import+Load job (14_BlueTrack_ScheduleImportLoadJob.sql) failed
   in its Import step every night. The Load step itself copes with either
   source's staging tables being empty (confirmed 2026-10-08 against a
   scratch database: a Self-Hosted-only and a Privilege-Cloud-only import
   each loaded cleanly through usp_RunFullLoad).

   This redefines usp_Import_All with two flags, appended after the
   existing parameters so every existing named-parameter call keeps
   working unchanged (both default to 1, i.e. today's behavior):
     @ImportPrivilegeCloud BIT = 1
     @ImportSelfHosted     BIT = 1
   For a source that's switched off:
     - its parameters may be NULL (the EVD database name, or the CSV paths);
     - its stg_pc_* / stg_sh_* staging tables are EMPTIED on every import,
       so data from an earlier setup or a test load never keeps flowing
       into the warehouse.
   For a source that's switched on, a missing export still fails the import
   (and so the job), with a message naming what's missing: a NULL path or
   EVD name is rejected up front, an EVD database that doesn't exist on
   this instance is rejected by name, and a missing CSV fails in BULK
   INSERT with SQL Server's own "Cannot bulk load ... could not be opened"
   error naming the file.

   stg_discovered_accounts is NOT touched here: it's filled by AD Account
   Discovery, not by either CyberArk source.
   ============================================================================ */

USE $DatabaseName$;
GO

CREATE OR ALTER PROCEDURE usp_Clear_Staging_PrivilegeCloud
AS
BEGIN
    SET NOCOUNT ON;
    TRUNCATE TABLE stg_pc_platforms;
    TRUNCATE TABLE stg_pc_users;
    TRUNCATE TABLE stg_pc_groups;
    TRUNCATE TABLE stg_pc_groupmembers;
    TRUNCATE TABLE stg_pc_safes;
    TRUNCATE TABLE stg_pc_accounts;
    TRUNCATE TABLE stg_pc_entitlements;
END
GO

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
END
GO

/* ============================================================================
   Single entry point: imports the configured sources' staging data and
   empties the other's. Still does NOT call usp_RunFullLoad -- import and
   load stay two explicit steps (see 07_BlueTrack_SourceImport.sql).
   ============================================================================ */
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
    @ImportSelfHosted     BIT = 1
)
AS
BEGIN
    SET NOCOUNT ON;

    IF ISNULL(@ImportPrivilegeCloud, 0) = 0 AND ISNULL(@ImportSelfHosted, 0) = 0
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
        EXEC usp_Import_SelfHosted_EVD @EVDDatabaseName = @EVDDatabaseName;
    ELSE
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

PRINT '42_BlueTrack_ImportSourceSelection.sql complete.';
PRINT 'Self-Hosted only:     EXEC usp_Import_All @EVDDatabaseName = N''YourEVD'', @ImportPrivilegeCloud = 0;';
PRINT 'Privilege Cloud only: EXEC usp_Import_All @ImportSelfHosted = 0, @PlatformsFile = ..., @EntitlementsExportDate = ...;';
