/* ============================================================================
   59_BlueTrack_AdDiscoveryRuns.sql

   RUN THIS AFTER 01-58. Guarded -- safe to re-run.

   - D-317: AD Account Discovery becomes a data source on Admin > Data
     Sources. It ran once a day counted from whenever the app started (so it
     moved with every app pool recycle) and recorded nothing. Now it runs at
     web.app_config.AdDiscoveryRunTime (server time, default 04:30, after the
     04:00 data feeds), can be run now from Data Sources, and each run is a
     row in web.ad_discovery_run: who or what started it, when, how it ended,
     how many domains were read and accounts found, and any problem.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.app_config', 'AdDiscoveryRunTime') IS NULL
    ALTER TABLE web.app_config ADD
        AdDiscoveryRunTime TIME(0) NOT NULL CONSTRAINT DF_app_config_AdDiscoveryRunTime DEFAULT '04:30';
GO

IF OBJECT_ID('web.ad_discovery_run', 'U') IS NULL
    CREATE TABLE web.ad_discovery_run (
        AdDiscoveryRunKey INT IDENTITY(1,1) PRIMARY KEY,
        TriggeredBy       NVARCHAR(200)  NOT NULL,   -- 'Schedule', or the user who chose Run now
        StartedAt         DATETIME2      NOT NULL CONSTRAINT DF_ad_discovery_run_StartedAt DEFAULT SYSUTCDATETIME(),
        FinishedAt        DATETIME2      NULL,
        Status            NVARCHAR(20)   NOT NULL CONSTRAINT DF_ad_discovery_run_Status DEFAULT 'Running',
        DomainCount       INT            NULL,       -- enabled LDAP domains
        DomainsRead       INT            NULL,       -- of those, read without a failure
        AccountsFound     INT            NULL,       -- members of inventoried Access Groups, over all domains read
        Message           NVARCHAR(2000) NULL,
        CONSTRAINT CK_ad_discovery_run_Status CHECK (Status IN ('Running', 'Succeeded', 'Partial', 'Failed', 'Skipped'))
    );
GO

PRINT '59_BlueTrack_AdDiscoveryRuns.sql complete.';
GO
