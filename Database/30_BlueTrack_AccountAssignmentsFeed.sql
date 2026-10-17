/* ============================================================================
   30_BlueTrack_AccountAssignmentsFeed.sql

   RUN THIS AFTER 01-29. Guarded -- safe to re-run.

   D-241: a data feed that assigns accounts to Applications (the scheduled
   form of the Account assignments CSV import, D-239). web.data_feed's
   FeedType CHECK constraint gets the AccountAssignments value.
   ============================================================================ */

USE $DatabaseName$;
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_data_feed_FeedType'
           AND definition NOT LIKE '%AccountAssignments%')
BEGIN
    ALTER TABLE web.data_feed DROP CONSTRAINT CK_data_feed_FeedType;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_data_feed_FeedType')
BEGIN
    ALTER TABLE web.data_feed ADD CONSTRAINT CK_data_feed_FeedType CHECK (FeedType IN (
        'TargetInventory', 'AccessGroupInventory', 'AccessGroupTargetMap',
        'AccountAccessGroupMembership', 'AccountTargetMap', 'Applications', 'SafeAssignments',
        'AccountInventory', 'AccountAssignments'));
END
GO
