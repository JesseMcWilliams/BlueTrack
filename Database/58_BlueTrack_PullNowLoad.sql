/* ============================================================================
   58_BlueTrack_PullNowLoad.sql

   RUN THIS AFTER 01-57. Guarded -- safe to re-run.

   - D-302: a Pull now that succeeds starts the nightly Import and Load SQL
     Agent job (Database/Manual/04) at once. The app's SQL account can't
     run the Import and Load itself (it truncates staging and bulk-loads
     Privilege Cloud files), so it starts the job, which runs as the
     Agent's account. That needs the app's login in msdb's
     SQLAgentOperatorRole (Database/Manual/08; installer step
     Db.AppPoolAccess). The Agent doesn't start a job that is already
     running, so a Pull now's load and the nightly run never overlap.
   - Each pull records when the app asked for the load and, when the job
     couldn't be started, why (LoadRequestedAt, LoadStartError). The load's
     own result is the dbo.etl_run_log Import and Load rows that follow.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('dbo.cyberark_direct_pull', 'LoadRequestedAt') IS NULL
    ALTER TABLE dbo.cyberark_direct_pull ADD
        LoadRequestedAt DATETIME2      NULL,   -- a Pull now that succeeded asked the Agent to start the Import and Load job
        LoadStartError  NVARCHAR(2000) NULL;   -- why the job couldn't be started (no grant, no job, Agent stopped, already running)
GO

PRINT '58_BlueTrack_PullNowLoad.sql complete.';
GO
