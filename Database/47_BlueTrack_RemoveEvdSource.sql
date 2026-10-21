/* ============================================================================
   47_BlueTrack_RemoveEvdSource.sql

   RUN THIS AFTER 01-46. Refuses to run on a site whose Self-Hosted source is
   still the EVD (see below); otherwise guarded and safe to re-run.

   D-278 (Claude_Docs/Design_CyberArk-Direct-Integration.md): phase 5 of
   moving off the EVD. Self-Hosted Direct is the only Self-Hosted source:

   - The EVD import (usp_Import_SelfHosted_EVD) and its setting
     (web.cyberark_source_config.EvdDatabaseName) are removed; SelfHostedSource
     can only be 'Direct'. usp_Import_All and usp_Import_Configured lose their
     EVD branches.
   - The shadow pull and comparison (D-271, D-272), which needed the EVD, are
     removed: their settings (ShadowPullEnabled, ShadowPullTime), the pull log's
     IsShadow and the shadow pulls in it, vw_shd_comparison and the two
     comparison procedures. The comparison's tables and data are kept, and so
     is web.shd_comparison_mark: dbo.vw_selfhosted_internal_safe (script 45)
     reads its Internal marks.
   - The stg_sh_* tables stay: a Direct pull fills them and the Load reads them.

   A site still importing Self-Hosted from the EVD must switch to Direct first
   (User_Docs/Admin_DeploymentRunbook.md, "Moving Self-Hosted from the EVD to
   Direct"). The installer checks this before it changes anything; this
   script's first batch refuses too, before anything is changed, so the
   script isn't recorded as run and runs once the site has switched.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.cyberark_source_config', 'SelfHostedSource') IS NOT NULL
   AND EXISTS (SELECT 1 FROM web.cyberark_source_config WHERE ImportSelfHosted = 1 AND SelfHostedSource <> N'Direct')
    THROW 50000, 'This BlueTrack version has no EVD source, and this site still imports Self-Hosted from the EVD. Switch the Self-Hosted source to Direct first (Admin_DeploymentRunbook.md, "Moving Self-Hosted from the EVD to Direct"), then upgrade again. Nothing was changed.', 1;
GO

/* ---- 1. The shadow pull and comparison -------------------------------------------- */

-- Shadow pulls must never look like real ones to the import's gate or the change guard.
IF COL_LENGTH('dbo.cyberark_direct_pull', 'IsShadow') IS NOT NULL
    EXEC (N'DELETE FROM dbo.cyberark_direct_pull WHERE IsShadow = 1;');
GO

DROP PROCEDURE IF EXISTS dbo.usp_Compare_SelfHostedShadow;
DROP PROCEDURE IF EXISTS dbo.usp_Apply_ShadowComparisonMarks;
DROP VIEW IF EXISTS dbo.vw_shd_comparison;
GO

IF OBJECT_ID('dbo.DF_cyberark_direct_pull_IsShadow', 'D') IS NOT NULL
    ALTER TABLE dbo.cyberark_direct_pull DROP CONSTRAINT DF_cyberark_direct_pull_IsShadow;
IF COL_LENGTH('dbo.cyberark_direct_pull', 'IsShadow') IS NOT NULL
    ALTER TABLE dbo.cyberark_direct_pull DROP COLUMN IsShadow;
GO

IF OBJECT_ID('web.DF_cyberark_source_config_ShadowPullEnabled', 'D') IS NOT NULL
    ALTER TABLE web.cyberark_source_config DROP CONSTRAINT DF_cyberark_source_config_ShadowPullEnabled;
IF COL_LENGTH('web.cyberark_source_config', 'ShadowPullEnabled') IS NOT NULL
    ALTER TABLE web.cyberark_source_config DROP COLUMN ShadowPullEnabled;
IF OBJECT_ID('web.DF_cyberark_source_config_ShadowPullTime', 'D') IS NOT NULL
    ALTER TABLE web.cyberark_source_config DROP CONSTRAINT DF_cyberark_source_config_ShadowPullTime;
IF COL_LENGTH('web.cyberark_source_config', 'ShadowPullTime') IS NOT NULL
    ALTER TABLE web.cyberark_source_config DROP COLUMN ShadowPullTime;
GO

/* ---- 2. The EVD source --------------------------------------------------------------- */

DROP PROCEDURE IF EXISTS dbo.usp_Import_SelfHosted_EVD;
GO

IF COL_LENGTH('web.cyberark_source_config', 'EvdDatabaseName') IS NOT NULL
    ALTER TABLE web.cyberark_source_config DROP COLUMN EvdDatabaseName;
GO

-- A row with Self-Hosted off may still say 'Evd' (the old default); Direct is the only source now.
UPDATE web.cyberark_source_config SET SelfHostedSource = N'Direct' WHERE SelfHostedSource <> N'Direct';
IF OBJECT_ID('web.CK_cyberark_source_config_SelfHostedSource', 'C') IS NOT NULL
    ALTER TABLE web.cyberark_source_config DROP CONSTRAINT CK_cyberark_source_config_SelfHostedSource;
IF OBJECT_ID('web.DF_cyberark_source_config_SelfHostedSource', 'D') IS NOT NULL
    ALTER TABLE web.cyberark_source_config DROP CONSTRAINT DF_cyberark_source_config_SelfHostedSource;
ALTER TABLE web.cyberark_source_config ADD
    CONSTRAINT DF_cyberark_source_config_SelfHostedSource DEFAULT N'Direct' FOR SelfHostedSource,
    CONSTRAINT CK_cyberark_source_config_SelfHostedSource CHECK (SelfHostedSource = N'Direct');
GO

/* usp_Import_All: as in 38_BlueTrack_SelfHostedDirect.sql, without the EVD. Self-Hosted is staged by the Direct pull
   (@SelfHostedStaged); with it off, the Self-Hosted staging tables are cleared. */
CREATE OR ALTER PROCEDURE usp_Import_All (
    @PlatformsFile     NVARCHAR(500) = NULL,
    @UsersFile         NVARCHAR(500) = NULL,
    @GroupsFile        NVARCHAR(500) = NULL,
    @GroupMembersFile  NVARCHAR(500) = NULL,
    @SafesFile         NVARCHAR(500) = NULL,
    @AccountsFile      NVARCHAR(500) = NULL,
    @EntitlementsFile  NVARCHAR(500) = NULL,
    @EntitlementsExportDate DATE = NULL,
    @ImportPrivilegeCloud BIT = 1,
    @SelfHostedStaged     BIT = 0   -- D-248: Self-Hosted Direct filled the stg_sh_* tables already; leave them alone
)
AS
BEGIN
    SET NOCOUNT ON;

    IF ISNULL(@ImportPrivilegeCloud, 0) = 0 AND ISNULL(@SelfHostedStaged, 0) = 0
        THROW 50000, 'usp_Import_All: Privilege Cloud is off and no Self-Hosted Direct pull is staged -- at least one source must be imported.', 1;

    -- Validate everything before importing anything, so a misconfigured
    -- run fails before it has emptied or half-filled any staging table.
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

    IF ISNULL(@SelfHostedStaged, 0) = 0
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

/* usp_Import_Configured: as in 42_BlueTrack_SelfHostedShadowPull.sql, without the EVD: with Self-Hosted on, the latest
   Direct pull must have succeeded within 20 hours (D-248), or the night fails. */
CREATE OR ALTER PROCEDURE dbo.usp_Import_Configured
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @EtlRunKey BIGINT;
    INSERT INTO dbo.etl_run_log (StepName) VALUES ('Import');
    SET @EtlRunKey = SCOPE_IDENTITY();

    BEGIN TRY
        DECLARE @SelfHostedStaged BIT = 0;
        DECLARE @ImportPrivilegeCloud BIT, @ImportSelfHosted BIT, @Folder NVARCHAR(400),
                @Platforms NVARCHAR(200), @Users NVARCHAR(200), @Groups NVARCHAR(200), @GroupMembers NVARCHAR(200),
                @Safes NVARCHAR(200), @Accounts NVARCHAR(200), @Entitlements NVARCHAR(200);

        SELECT @ImportPrivilegeCloud = ImportPrivilegeCloud, @ImportSelfHosted = ImportSelfHosted,
               @Folder = ExportFolderPath,
               @Platforms = PlatformsFileName, @Users = UsersFileName, @Groups = GroupsFileName,
               @GroupMembers = GroupMembersFileName, @Safes = SafesFileName, @Accounts = AccountsFileName,
               @Entitlements = EntitlementsFileName
        FROM web.cyberark_source_config
        WHERE CyberArkSourceConfigKey = 1;

        IF @@ROWCOUNT = 0
            THROW 50000, 'usp_Import_Configured: there are no CyberArk source settings. Set them on Admin > Data Sources, or re-run the installer''s Db.NightlyJob step.', 1;

        /* D-248: the API pulls PVWA into the stg_sh_* tables ahead of this job (at DirectPullTime). The Import only checks
           that the latest pull succeeded and is recent. A rejected or failed pull fails the night, so the Load doesn't
           run on stale or incomplete data and NightlyImportFailed mails the reason. */
        IF @ImportSelfHosted = 1
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
            @PlatformsFile = @PlatformsFile,
            @UsersFile = @UsersFile,
            @GroupsFile = @GroupsFile,
            @GroupMembersFile = @GroupMembersFile,
            @SafesFile = @SafesFile,
            @AccountsFile = @AccountsFile,
            @EntitlementsFile = @EntitlementsFile,
            @EntitlementsExportDate = @Today,
            @ImportPrivilegeCloud = @ImportPrivilegeCloud,
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

PRINT '47_BlueTrack_RemoveEvdSource.sql complete.';
GO
