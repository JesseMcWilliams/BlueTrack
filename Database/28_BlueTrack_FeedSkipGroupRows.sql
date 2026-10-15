/* ============================================================================
   28_BlueTrack_FeedSkipGroupRows.sql

   RUN THIS AFTER 01-27. Guarded -- safe to re-run.

   D-237: a pending-accounts export lists local and domain groups beside the
   accounts. Their user names are written as "*<name> Group*" (a "*" at each
   end, the word "group" inside). web.data_feed gets SkipGroupRows: when it is
   on, an AccountInventory feed skips those rows. It is off for every feed
   until someone turns it on.
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.data_feed', 'SkipGroupRows') IS NULL
    ALTER TABLE web.data_feed ADD SkipGroupRows BIT NOT NULL
        CONSTRAINT DF_data_feed_SkipGroupRows DEFAULT 0;
GO
