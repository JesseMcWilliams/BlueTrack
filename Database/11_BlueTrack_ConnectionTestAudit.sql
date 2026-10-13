/* ============================================================================
   11_BlueTrack_ConnectionTestAudit.sql

   RUN THIS AFTER 01-10. Guarded -- safe to re-run.

   D-210: the Secrets Store and Credentials pages' Test buttons are
   audit-logged, under a new ConnectionTest event type, decided by the user
   on 2026-10-13. A test retrieves a secret for real, so who asked for which
   Safe/Folder/Object (or credential), and the outcome, is recorded. Tests
   are reads, not edits, so they get their own type rather than FieldEdit.
   ============================================================================ */

USE $DatabaseName$;
GO

INSERT INTO web.dim_audit_event_type (EventTypeName, Description)
SELECT 'ConnectionTest', 'A Test button retrieved a secret or credential (what was asked, and the outcome)'
WHERE NOT EXISTS (SELECT 1 FROM web.dim_audit_event_type WHERE EventTypeName = 'ConnectionTest');
GO
