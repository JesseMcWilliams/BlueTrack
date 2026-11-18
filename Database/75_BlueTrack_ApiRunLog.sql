/* ============================================================================
   75_BlueTrack_ApiRunLog.sql

   RUN THIS AFTER 01-74. Guarded -- safe to re-run.

   D-361 (the user, 2026-11-18, after a customer's nightly pull stopped for
   four nights with no trace: "We need to make sure there is an easy way to
   troubleshoot issues like this."):
   - web.api_run: one row each time the API starts (IIS starts or recycles
     its worker process): when, on which machine, the worker's process ID,
     the version, and when it stopped, if it stopped cleanly. A gap between
     one row's StoppedAt and the next row's StartedAt is time the API, and so
     everything it schedules (the Self-Hosted Direct pull, AD discovery, data
     feeds, notifications, backups), wasn't running. A row with no StoppedAt
     that isn't the newest one ended without a clean stop (a crash, or the
     process was killed).
   - Small by design (the customer's server is short of memory): one insert
     at start and one update at stop; the API deletes rows older than 180
     days when it starts.
   ============================================================================ */
USE $DatabaseName$;
GO

IF OBJECT_ID('web.api_run', 'U') IS NULL
    CREATE TABLE web.api_run (
        ApiRunKey   BIGINT IDENTITY(1,1) CONSTRAINT PK_api_run PRIMARY KEY,
        StartedAt   DATETIME2     NOT NULL CONSTRAINT DF_api_run_StartedAt DEFAULT SYSUTCDATETIME(),
        StoppedAt   DATETIME2     NULL,
        MachineName NVARCHAR(128) NOT NULL,
        ProcessId   INT           NOT NULL,
        Version     NVARCHAR(100) NOT NULL
    );
GO
