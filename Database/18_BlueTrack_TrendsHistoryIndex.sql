/* ============================================================================
   18_BlueTrack_TrendsHistoryIndex.sql

   RUN THIS AFTER 01-17. Guarded -- safe to re-run.

   D-221: the Reports > Trends page (GET /api/reports/trends) reads
   dbo.fact_account_progress_history by snapshot date range. That table had
   only its clustered primary key on HistoryKey, so every Trends request
   (and usp_Load_FactAccountProgressHistory's own same-day DELETE) scanned
   the whole table. It grows by one row per tracked account per day: about
   2,500 rows a day on the first real install (2026-10-13), so roughly
   900,000 rows a year.

     IX_fact_account_progress_history_SnapshotDate
         on (SnapshotDateKey), covering AccountKey, StageKey, StatusKey and
         RiskLevelKey, so a date-range request reads only the days asked
         for, from the index alone.
   ============================================================================ */

USE $DatabaseName$;
GO

IF NOT EXISTS (
    SELECT 1 FROM sys.indexes
    WHERE object_id = OBJECT_ID('dbo.fact_account_progress_history')
      AND name = 'IX_fact_account_progress_history_SnapshotDate'
)
    CREATE NONCLUSTERED INDEX IX_fact_account_progress_history_SnapshotDate
        ON dbo.fact_account_progress_history (SnapshotDateKey)
        INCLUDE (AccountKey, StageKey, StatusKey, RiskLevelKey);
GO
