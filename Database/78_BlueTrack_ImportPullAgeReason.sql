/* ============================================================================
   78_BlueTrack_ImportPullAgeReason.sql

   RUN THIS AFTER 01-77. Safe to re-run (CREATE OR ALTER).

   D-364 (follows D-361 to D-363): when the nightly Import fails because the
   latest Self-Hosted Direct pull is more than 20 hours old, the error (the
   SQL Agent job history and the NightlyImportFailed email) now says that no
   pull has started since, that the API starts the pull, whether the API's
   pull scheduler is checking now (web.api_run), and where to look. Until
   now it gave only the pull's age, and the customer couldn't tell the pull
   hadn't run because the API wasn't running. usp_Import_Configured as in
   47, with only that message changed. Two reads of web.api_run (a few
   hundred rows at most), and only on that failure.
   ============================================================================ */
USE $DatabaseName$;
GO

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
            BEGIN
                /* D-364: say why. The pull is started by the API's scheduler (web.api_run, D-361/D-362), so an old pull
                   with none since nearly always means the API wasn't running at the pull time. */
                DECLARE @SchedulerChecked DATETIME2, @ApiStarted DATETIME2;
                SELECT @SchedulerChecked = MAX(PullSchedulerCheckedAt), @ApiStarted = MAX(StartedAt) FROM web.api_run;
                SET @PullError = N'Self-Hosted Direct: the latest successful PVWA pull is more than 20 hours old (' + CONVERT(NVARCHAR(30), @PullCompleted, 120)
                    + N' UTC), and no pull has started since. The BlueTrack API starts the pull, so it runs only while IIS has the API loaded. '
                    + CASE
                        WHEN @ApiStarted IS NULL THEN N''
                        WHEN @SchedulerChecked IS NULL OR @SchedulerChecked < DATEADD(MINUTE, -10, SYSUTCDATETIME())
                            THEN N'The API''s pull scheduler last checked at ' + ISNULL(CONVERT(NVARCHAR(30), @SchedulerChecked, 120) + N' UTC', N'(never)')
                               + N' (the API last started at ' + CONVERT(NVARCHAR(30), @ApiStarted, 120) + N' UTC), so the API is not running now. '
                        ELSE N'The API is running now (it started at ' + CONVERT(NVARCHAR(30), @ApiStarted, 120) + N' UTC). '
                      END
                    + N'See API starts on Admin > Deployment, run Pull now on Admin > Data Sources, and check the BlueTrack application pool in IIS (Admin Operations Guide, "A nightly pull didn''t run").';
            END
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

PRINT '78_BlueTrack_ImportPullAgeReason.sql complete.';
GO
