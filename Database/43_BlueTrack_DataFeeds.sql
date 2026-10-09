/* ============================================================================
   43_BlueTrack_DataFeeds.sql

   RUN THIS AFTER 01-42. Guarded (OBJECT_ID / COL_LENGTH / NOT EXISTS
   checks) -- safe to re-run, per D-58's incremental-script convention.

   Data Sources phase 2 (Claude_Docs/Planning_Data-Sources.md, D-181):
   scheduled CSV feeds that run the existing import pipelines
   (App/Api/Imports/, phase 1) from a folder, nightly or on demand.

     web.data_feed       one row per feed: which import it runs (FeedType),
                         where its file is, an optional D-105 mapping profile.
     web.data_feed_run   one row per feed per run: outcome, counts, row errors.
     web.app_config      five new settings: the nightly run time, run-history
                         retention, and business hours (Run now warns inside
                         them). Defaults agreed 2026-10-08/09: 04:00 (after
                         the 02:00 Import+Load and 03:00 audit purge jobs),
                         15 days, 07:00-18:00 Monday to Friday.
     ManageDataSources   new permission, granted to the bootstrap Admin role.
     System user         'BlueTrack Data Feeds (system)': scheduled runs are
                         attributed to it (D-181). Created under the
                         WindowsIntegrated provider with ExternalIdentifier
                         S-1-0-0, Windows' NULL SID, which no real account
                         ever has -- so nobody can sign in as it, and no new
                         (enable-able) identity provider row is needed.
   ============================================================================ */

USE $DatabaseName$;
GO

IF OBJECT_ID('web.data_feed', 'U') IS NULL
BEGIN
    CREATE TABLE web.data_feed (
        DataFeedKey            INT IDENTITY(1,1) PRIMARY KEY,
        DisplayName              NVARCHAR(200)    NOT NULL,
        FeedType                   NVARCHAR(50)     NOT NULL,
        FolderPath                   NVARCHAR(500)    NOT NULL,
        FileNamePattern                 NVARCHAR(260)    NOT NULL,   -- may contain {yyyy-MM-dd} and * ; newest match wins
        ImportMappingProfileKey            INT              NULL REFERENCES web.import_mapping_profile(ImportMappingProfileKey),
        IsEnabled                             BIT              NOT NULL DEFAULT 1,
        DisplayOrder                            INT              NOT NULL DEFAULT 0,
        CreatedBy                                 INT              NULL REFERENCES web.app_user(UserKey),
        CreatedDate                                 DATETIME2        NOT NULL DEFAULT SYSUTCDATETIME(),
        ModifiedBy                                    INT              NULL REFERENCES web.app_user(UserKey),
        ModifiedDate                                    DATETIME2        NULL,
        CONSTRAINT UQ_data_feed_DisplayName UNIQUE (DisplayName),
        CONSTRAINT CK_data_feed_FeedType CHECK (FeedType IN (
            'TargetInventory', 'AccessGroupInventory', 'AccessGroupTargetMap',
            'AccountAccessGroupMembership', 'AccountTargetMap', 'Applications', 'SafeAssignments'))
    );
END
GO

IF OBJECT_ID('web.data_feed_run', 'U') IS NULL
BEGIN
    CREATE TABLE web.data_feed_run (
        DataFeedRunKey         BIGINT IDENTITY(1,1) PRIMARY KEY,
        DataFeedKey              INT              NOT NULL REFERENCES web.data_feed(DataFeedKey) ON DELETE CASCADE,
        TriggerType                NVARCHAR(20)     NOT NULL,            -- 'Schedule' / 'RunNow'
        TriggeredBy                  INT              NULL REFERENCES web.app_user(UserKey),
        StartedAt                      DATETIME2        NOT NULL,
        FinishedAt                       DATETIME2        NULL,
        Outcome                            NVARCHAR(30)     NOT NULL,       -- 'Succeeded' / 'CompletedWithErrors' / 'Failed'
        FileName                             NVARCHAR(500)    NULL,
        TotalRows                              INT              NULL,
        ErrorRows                                INT              NULL,
        Summary                                    NVARCHAR(500)    NULL,  -- one-line human summary of the counts
        ResultJson                                   NVARCHAR(MAX)    NULL,  -- the import's own result, row errors included
        ErrorMessage                                   NVARCHAR(2000)   NULL,  -- why a Failed run failed (no file, unreadable, ...)
        CONSTRAINT CK_data_feed_run_TriggerType CHECK (TriggerType IN ('Schedule', 'RunNow')),
        CONSTRAINT CK_data_feed_run_Outcome CHECK (Outcome IN ('Succeeded', 'CompletedWithErrors', 'Failed'))
    );
    CREATE INDEX IX_data_feed_run_Feed_Started ON web.data_feed_run (DataFeedKey, StartedAt DESC);
END
GO

IF COL_LENGTH('web.app_config', 'DataFeedRunTime') IS NULL
    ALTER TABLE web.app_config ADD DataFeedRunTime TIME(0) NOT NULL CONSTRAINT DF_app_config_DataFeedRunTime DEFAULT '04:00';
GO
IF COL_LENGTH('web.app_config', 'DataFeedRunRetentionDays') IS NULL
    ALTER TABLE web.app_config ADD DataFeedRunRetentionDays INT NOT NULL CONSTRAINT DF_app_config_DataFeedRunRetentionDays DEFAULT 15;
GO
IF COL_LENGTH('web.app_config', 'BusinessHoursStart') IS NULL
    ALTER TABLE web.app_config ADD BusinessHoursStart TIME(0) NOT NULL CONSTRAINT DF_app_config_BusinessHoursStart DEFAULT '07:00';
GO
IF COL_LENGTH('web.app_config', 'BusinessHoursEnd') IS NULL
    ALTER TABLE web.app_config ADD BusinessHoursEnd TIME(0) NOT NULL CONSTRAINT DF_app_config_BusinessHoursEnd DEFAULT '18:00';
GO
-- Comma-separated three-letter English day names, e.g. 'Mon,Tue,Wed,Thu,Fri'.
IF COL_LENGTH('web.app_config', 'BusinessDays') IS NULL
    ALTER TABLE web.app_config ADD BusinessDays NVARCHAR(30) NOT NULL CONSTRAINT DF_app_config_BusinessDays DEFAULT 'Mon,Tue,Wed,Thu,Fri';
GO

IF NOT EXISTS (SELECT 1 FROM web.app_permission WHERE PermissionName = 'ManageDataSources')
BEGIN
    INSERT INTO web.app_permission (PermissionName, Description)
    VALUES ('ManageDataSources', 'Manage data feeds: add, edit, test and run scheduled CSV imports, and view their history');
END
GO

DECLARE @AdminRoleKey INT = (SELECT AppRoleKey FROM web.app_role WHERE RoleName = 'Admin');
DECLARE @ManageDataSourcesKey INT = (SELECT PermissionKey FROM web.app_permission WHERE PermissionName = 'ManageDataSources');
IF @AdminRoleKey IS NOT NULL AND NOT EXISTS (SELECT 1 FROM web.role_permission WHERE RoleKey = @AdminRoleKey AND PermissionKey = @ManageDataSourcesKey)
BEGIN
    INSERT INTO web.role_permission (RoleKey, PermissionKey) VALUES (@AdminRoleKey, @ManageDataSourcesKey);
END
GO

DECLARE @WinIntProviderKey INT = (SELECT ProviderKey FROM web.identity_provider_config WHERE ProviderType = 'WindowsIntegrated');
IF @WinIntProviderKey IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM web.app_user WHERE ProviderKey = @WinIntProviderKey AND ExternalIdentifier = 'S-1-0-0')
BEGIN
    INSERT INTO web.app_user (ProviderKey, ExternalIdentifier, DisplayName)
    VALUES (@WinIntProviderKey, 'S-1-0-0', 'BlueTrack Data Feeds (system)');
END
GO

PRINT '43_BlueTrack_DataFeeds.sql complete.';
