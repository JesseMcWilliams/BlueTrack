/* ============================================================================
   39_BlueTrack_AuditLogPurgeProcedure.sql

   RUN THIS AFTER 01-38. Safe to re-run (CREATE OR ALTER -- pure logic, no
   data of its own, unlike a table).

   D-62 (Design_Audit-Logging.md) designed this purge mechanism back on
   2026-08-27 -- web.audit_purge_log (its own run-history table) was built
   the same day in 08_BlueTrack_WebSchema.sql, but usp_PurgeAuditLog itself
   was only ever mentioned in that table's own header comment, never
   actually written. Found 2026-09-21 while documenting day-2 operations
   (User_Docs/Admin_OperationsGuide.md) -- audit_config.RetentionDays was a stored
   setting with no enforcement anywhere; setting it did nothing. This
   script closes that gap: the procedure itself. See
   40_BlueTrack_ScheduleAuditLogPurgeJob.sql for the SQL Agent job that
   calls it nightly -- that script, like 14/38 before it, must run via
   sqlcmd, never through App/Migrator, since it targets msdb.

   Deletes web.audit_field_change rows before their parent web.audit_event
   rows (FK dependency), for events older than
   RetentionDays before today. RowsPurged records only the audit_event
   count, matching Design_Audit-Logging.md's own column description
   ("How long audit records are kept" -- audit_event IS the record;
   audit_field_change rows are its detail, not counted separately).

   RetentionDays is nullable by design (08_BlueTrack_WebSchema.sql's own
   comment: "deliberately left NULL... must be set explicitly before the
   purge job is scheduled to run meaningfully") -- if it's still NULL when
   this runs, nothing is deleted. A 'Skipped' row is still written to
   audit_purge_log so a DBA checking that table can tell the job ran and
   chose not to purge, rather than not running at all. CutoffDate is
   NOT NULL on that table, so a Skipped run's CutoffDate is today's date --
   not meaningful on its own for that row, since Status/ErrorMessage carry
   the real explanation; kept simple rather than widening the schema for
   a nullable-in-one-case column.
   ============================================================================ */

USE $DatabaseName$;
GO

CREATE OR ALTER PROCEDURE dbo.usp_PurgeAuditLog
AS
BEGIN
    SET NOCOUNT ON;

    DECLARE @RetentionDays INT = (SELECT RetentionDays FROM web.audit_config);
    DECLARE @PurgeBatchId UNIQUEIDENTIFIER = NEWID();
    DECLARE @StartedAt DATETIME2 = SYSUTCDATETIME();

    IF @RetentionDays IS NULL
    BEGIN
        INSERT INTO web.audit_purge_log (PurgeBatchId, CutoffDate, RowsPurged, StartedAt, CompletedAt, Status, ErrorMessage)
        VALUES (@PurgeBatchId, CAST(SYSUTCDATETIME() AS DATE), NULL, @StartedAt, SYSUTCDATETIME(), 'Skipped', 'No RetentionDays configured on web.audit_config -- nothing purged.');
        RETURN;
    END

    DECLARE @CutoffDate DATE = DATEADD(DAY, -@RetentionDays, CAST(SYSUTCDATETIME() AS DATE));

    BEGIN TRY
        DELETE fc
        FROM web.audit_field_change fc
        JOIN web.audit_event ae ON ae.AuditEventKey = fc.AuditEventKey
        WHERE ae.OccurredAt < @CutoffDate;

        DELETE FROM web.audit_event WHERE OccurredAt < @CutoffDate;
        DECLARE @RowsPurged INT = @@ROWCOUNT;

        INSERT INTO web.audit_purge_log (PurgeBatchId, CutoffDate, RowsPurged, StartedAt, CompletedAt, Status, ErrorMessage)
        VALUES (@PurgeBatchId, @CutoffDate, @RowsPurged, @StartedAt, SYSUTCDATETIME(), 'Succeeded', NULL);
    END TRY
    BEGIN CATCH
        INSERT INTO web.audit_purge_log (PurgeBatchId, CutoffDate, RowsPurged, StartedAt, CompletedAt, Status, ErrorMessage)
        VALUES (@PurgeBatchId, @CutoffDate, NULL, @StartedAt, SYSUTCDATETIME(), 'Failed', ERROR_MESSAGE());
        THROW;
    END CATCH
END
GO

PRINT '39_BlueTrack_AuditLogPurgeProcedure.sql complete.';
