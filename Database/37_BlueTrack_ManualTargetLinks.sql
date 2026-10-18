/* ============================================================================
   37_BlueTrack_ManualTargetLinks.sql

   RUN THIS AFTER 01-36. Guarded -- safe to re-run.

   D-245: two existing targets can be linked by hand (Edit Target), not only
   through Target Match Review. web.target_link.LinkMethod gets a second value,
   'Manual' (a pair that was unlinked is linked again by updating its row).
   ============================================================================ */

USE $DatabaseName$;
GO

IF EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_target_link_Method' AND definition NOT LIKE '%Manual%')
BEGIN
    ALTER TABLE web.target_link DROP CONSTRAINT CK_target_link_Method;
END
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_target_link_Method')
BEGIN
    ALTER TABLE web.target_link ADD CONSTRAINT CK_target_link_Method CHECK (LinkMethod IN ('MatchReview', 'Manual'));
END
GO
