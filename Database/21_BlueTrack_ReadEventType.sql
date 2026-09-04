/* ============================================================================
   21_BlueTrack_ReadEventType.sql

   RUN THIS AFTER 01-20.

   dim_audit_event_type (Design_Audit_Logging.md) is a governed dimension
   table, extended the same way as prior gaps (D-55, D-61, D-68, and 13's
   ExceptionReviewExtended/ExceptionRevoked): D-35's LogReadEvents enforcement
   (D-83) needs an event type for a logged detail-view read, and none existed.

   Guarded -- safe to re-run.
   ============================================================================ */

USE $DatabaseName$;
GO

INSERT INTO web.dim_audit_event_type (EventTypeName, Description)
SELECT v.EventTypeName, v.Description
FROM (VALUES
    ('RecordViewed', 'A governed record''s detail view was read (only logged when audit_config.LogReadEvents is enabled)')
) AS v(EventTypeName, Description)
WHERE NOT EXISTS (
    SELECT 1 FROM web.dim_audit_event_type t WHERE t.EventTypeName = v.EventTypeName
);

PRINT 'dim_audit_event_type: added RecordViewed.';
