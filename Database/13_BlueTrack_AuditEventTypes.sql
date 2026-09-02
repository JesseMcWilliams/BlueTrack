/* ============================================================================
   13_BlueTrack_AuditEventTypes.sql

   RUN THIS AFTER 01-12.

   dim_audit_event_type (Design_Audit_Logging.md) is a governed dimension
   table, extensible the same way other dimension gaps have been fixed in
   this project (D-55, D-61, D-68) -- found missing while wiring real audit
   logging into the Risk Exceptions actions (create/extend-review/revoke):
   the original catalog covered creation (ExceptionApproved) but not the
   other two actions in that same workflow (Design_Risk_Exception_Tracking.md
   step 4, "re-approval or revocation").

   Guarded -- safe to re-run.
   ============================================================================ */

USE BlueTrack;
GO

INSERT INTO web.dim_audit_event_type (EventTypeName, Description)
SELECT v.EventTypeName, v.Description
FROM (VALUES
    ('ExceptionReviewExtended', 'A risk exception''s ReviewDate was extended (re-approval)'),
    ('ExceptionRevoked',        'A risk exception was revoked')
) AS v(EventTypeName, Description)
WHERE NOT EXISTS (
    SELECT 1 FROM web.dim_audit_event_type t WHERE t.EventTypeName = v.EventTypeName
);

PRINT 'dim_audit_event_type: added ExceptionReviewExtended/ExceptionRevoked.';
