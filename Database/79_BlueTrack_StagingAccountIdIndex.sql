/* ============================================================================
   79_BlueTrack_StagingAccountIdIndex.sql

   RUN THIS AFTER 01-78. Guarded -- safe to re-run.

   D-368 (the user, 2026-11-18, from a customer's resource report): an index
   on dbo.stg_shd_accounts (AccountId). After each Self-Hosted Direct pull,
   SelfHostedDirectRepository.PruneAccountDetailCacheAsync deletes the
   cached account details of accounts the pull no longer has, with
   NOT EXISTS (... stg_shd_accounts a WHERE a.AccountId = c.AccountId).
   With no index, SQL Server scanned the staging table for each cached
   account: 10 seconds of CPU and 3.96 million reads for 14,300 accounts,
   deleting nothing. The Load's joins from fact_account to the staged
   accounts by AccountId use it too. The staging table is replaced on each
   pull; the index costs little on that insert.
   ============================================================================ */
USE $DatabaseName$;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_stg_shd_accounts_AccountId' AND object_id = OBJECT_ID('dbo.stg_shd_accounts'))
    CREATE INDEX IX_stg_shd_accounts_AccountId ON dbo.stg_shd_accounts (AccountId);
GO

PRINT '79_BlueTrack_StagingAccountIdIndex.sql complete.';
GO
