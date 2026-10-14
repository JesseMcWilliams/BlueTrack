/* ============================================================================
   19_BlueTrack_MoreNotifications.sql

   RUN THIS AFTER 01-18. Guarded -- safe to re-run.

   D-222: three more email notifications (Design_Notifications.md), decided
   by the user on 2026-10-13.

     dbo.etl_run_log                one row per run of the Import
                                    (usp_Import_Configured) and the Load
                                    (usp_RunFullLoad): Started, then Succeeded
                                    or Failed with the error, the procedure
                                    and the line. Both procedures are
                                    redefined below, unchanged apart from
                                    writing it, so the API (which the SQL
                                    Agent job can't call) can see a failed or
                                    unfinished run. Readable through the app
                                    account's existing db_datareader, so no
                                    msdb grant is needed.
     web.dim_notification_type.IsEnabled
                                    enable or disable each type on the
                                    Notifications page. The two existing types
                                    stay on; the three new ones start off.
     web.notification_log.DedupKey  what one send was for (a run, a day, an
                                    ISO week): unique per type, so a run or a
                                    day is emailed once, across restarts and
                                    more than one API process.
     web.notification_config        DailyDigestTime (07:00), WeeklySummaryDay
                                    (Mon) and WeeklySummaryTime (07:00),
                                    server local time.
     Notification types             NightlyImportFailed,
                                    OverdueRiskExceptions, WeeklySummary.

   A later script that changes usp_RunFullLoad or usp_Import_Configured must
   keep the etl_run_log writes.
   ============================================================================ */

USE $DatabaseName$;
GO

IF OBJECT_ID('dbo.etl_run_log') IS NULL
BEGIN
    CREATE TABLE dbo.etl_run_log (
        EtlRunKey       BIGINT IDENTITY(1,1) CONSTRAINT PK_etl_run_log PRIMARY KEY,
        StepName        NVARCHAR(20)   NOT NULL CONSTRAINT CK_etl_run_log_StepName CHECK (StepName IN ('Import', 'Load')),
        StartedAt       DATETIME2      NOT NULL CONSTRAINT DF_etl_run_log_StartedAt DEFAULT SYSUTCDATETIME(),
        CompletedAt     DATETIME2      NULL,
        Status          NVARCHAR(20)   NOT NULL CONSTRAINT DF_etl_run_log_Status DEFAULT 'Started'
                                       CONSTRAINT CK_etl_run_log_Status CHECK (Status IN ('Started', 'Succeeded', 'Failed')),
        ErrorMessage    NVARCHAR(2000) NULL,
        ErrorProcedure  NVARCHAR(128)  NULL,
        ErrorLine       INT            NULL,
        StartedBy       NVARCHAR(256)  NOT NULL CONSTRAINT DF_etl_run_log_StartedBy DEFAULT ORIGINAL_LOGIN(),
        ProgramName     NVARCHAR(128)  NULL CONSTRAINT DF_etl_run_log_ProgramName DEFAULT APP_NAME()
    );
    CREATE NONCLUSTERED INDEX IX_etl_run_log_StartedAt ON dbo.etl_run_log (StartedAt) INCLUDE (StepName, Status, CompletedAt);
END
GO

IF COL_LENGTH('web.dim_notification_type', 'IsEnabled') IS NULL
    ALTER TABLE web.dim_notification_type ADD IsEnabled BIT NOT NULL
        CONSTRAINT DF_dim_notification_type_IsEnabled DEFAULT 1;
GO

IF COL_LENGTH('web.notification_log', 'DedupKey') IS NULL
    ALTER TABLE web.notification_log ADD DedupKey NVARCHAR(200) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('web.notification_log') AND name = 'UX_notification_log_Type_DedupKey')
    CREATE UNIQUE NONCLUSTERED INDEX UX_notification_log_Type_DedupKey
        ON web.notification_log (NotificationTypeKey, DedupKey)
        WHERE DedupKey IS NOT NULL;
GO

IF COL_LENGTH('web.notification_config', 'DailyDigestTime') IS NULL
    ALTER TABLE web.notification_config ADD DailyDigestTime TIME(0) NOT NULL
        CONSTRAINT DF_notification_config_DailyDigestTime DEFAULT '07:00';
GO

IF COL_LENGTH('web.notification_config', 'WeeklySummaryDay') IS NULL
    ALTER TABLE web.notification_config ADD WeeklySummaryDay NVARCHAR(3) NOT NULL
        CONSTRAINT DF_notification_config_WeeklySummaryDay DEFAULT 'Mon'
        CONSTRAINT CK_notification_config_WeeklySummaryDay CHECK (WeeklySummaryDay IN ('Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'));
GO

IF COL_LENGTH('web.notification_config', 'WeeklySummaryTime') IS NULL
    ALTER TABLE web.notification_config ADD WeeklySummaryTime TIME(0) NOT NULL
        CONSTRAINT DF_notification_config_WeeklySummaryTime DEFAULT '07:00';
GO

INSERT INTO web.dim_notification_type (NotificationTypeName, Description, IsEnabled)
SELECT 'NightlyImportFailed', 'The Import and Load (the nightly SQL Agent job, or a run by hand) failed, or has not finished 4 hours after it started (D-222). One email per run, within about 5 minutes.', 0
WHERE NOT EXISTS (SELECT 1 FROM web.dim_notification_type WHERE NotificationTypeName = 'NightlyImportFailed');

INSERT INTO web.dim_notification_type (NotificationTypeName, Description, IsEnabled)
SELECT 'OverdueRiskExceptions', 'Daily, at the daily digest time: the Active Risk Exceptions past their review date (Overdue Reviews, D-19). Not sent when none are overdue (D-222).', 0
WHERE NOT EXISTS (SELECT 1 FROM web.dim_notification_type WHERE NotificationTypeName = 'OverdueRiskExceptions');

INSERT INTO web.dim_notification_type (NotificationTypeName, Description, IsEnabled)
SELECT 'WeeklySummary', 'Weekly, on the weekly summary day and time: the KPI Summary and its change since last week, overdue counts and the last import (D-222).', 0
WHERE NOT EXISTS (SELECT 1 FROM web.dim_notification_type WHERE NotificationTypeName = 'WeeklySummary');
GO

/* usp_Import_Configured: as in 12_BlueTrack_CyberArkSourceConfig.sql, plus
   the etl_run_log row. */
CREATE OR ALTER PROCEDURE dbo.usp_Import_Configured
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @EtlRunKey BIGINT;
    INSERT INTO dbo.etl_run_log (StepName) VALUES ('Import');
    SET @EtlRunKey = SCOPE_IDENTITY();

    BEGIN TRY
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

/* usp_RunFullLoad: as in 06_BlueTrack_Baseline_WebLogic.sql, plus the
   etl_run_log row, written outside the load's transaction so a rollback
   keeps it. */
CREATE OR ALTER PROCEDURE usp_RunFullLoad
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @EtlRunKey BIGINT;
    INSERT INTO dbo.etl_run_log (StepName) VALUES ('Load');
    SET @EtlRunKey = SCOPE_IDENTITY();

    BEGIN TRY
        BEGIN TRANSACTION;

        EXEC usp_Load_DimLocation;
        EXEC usp_Load_DimPlatform;
        EXEC usp_Load_DimSafe;
        EXEC usp_Load_DimUser;
        EXEC usp_Load_DimGroup;
        EXEC usp_Load_GroupMembership;
        EXEC usp_Load_FactAccount;
        EXEC usp_Load_FactAccountProgress;
        EXEC usp_Load_AccountProgressAutoAdvance;
        EXEC usp_Load_FactSafeEntitlement;
        EXEC usp_Load_AccountReconciliation;
        EXEC usp_Load_FactAccountProgressHistory;
        EXEC usp_DeriveTargetsFromAccountAddress;   -- D-123 (51_)
        EXEC usp_DeriveAccountTargetMap_FromPendingSafes;
        EXEC usp_DeriveAccountTargetMap_FromAddress;    -- D-193 (51_)
        EXEC usp_RecalculateRiskScores;

        COMMIT TRANSACTION;

        UPDATE dbo.etl_run_log SET Status = 'Succeeded', CompletedAt = SYSUTCDATETIME() WHERE EtlRunKey = @EtlRunKey;
    END TRY
    BEGIN CATCH
        IF XACT_STATE() <> 0 ROLLBACK TRANSACTION;
        UPDATE dbo.etl_run_log
        SET Status = 'Failed', CompletedAt = SYSUTCDATETIME(), ErrorMessage = LEFT(ERROR_MESSAGE(), 2000),
            ErrorProcedure = ERROR_PROCEDURE(), ErrorLine = ERROR_LINE()
        WHERE EtlRunKey = @EtlRunKey;
        THROW;
    END CATCH
END
GO
