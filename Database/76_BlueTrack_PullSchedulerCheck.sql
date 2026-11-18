/* ============================================================================
   76_BlueTrack_PullSchedulerCheck.sql

   RUN THIS AFTER 01-75. Guarded -- safe to re-run.

   D-362 (follows D-361): web.api_run.PullSchedulerCheckedAt, the last time
   this run's Self-Hosted Direct pull scheduler woke and checked whether the
   pull was due (it wakes every minute). Admin > Data Sources shows it with
   the next pull time, and warns when it's more than a few minutes old: the
   scheduler has stopped, so the nightly pull won't start. One single-row
   update a minute (the customer's server is short of memory: no new table,
   no index).
   ============================================================================ */
USE $DatabaseName$;
GO

IF COL_LENGTH('web.api_run', 'PullSchedulerCheckedAt') IS NULL
    ALTER TABLE web.api_run ADD PullSchedulerCheckedAt DATETIME2 NULL;
GO
