/* ============================================================================
   07_BlueTrack_SqlServerAudit.sql

   D-295 (the user's choice of 2026-10-21): SQL Server Audit records every
   UPDATE and DELETE on the audit log's tables -- by anyone, a DBA or a
   sysadmin included -- in the Windows Security log, where it is harder to
   change quietly than anything inside the database. Together with the
   seals (Database/54) and SIEM forwarding (D-234), it is how a changed
   audit log is noticed and who changed it is known.

   Like the other scripts in Database/Manual, App/Migrator never runs this.
   Deploy/Install-BlueTrack.ps1 runs it (step Db.SqlServerAudit), after it
   has prepared Windows; by hand:

       sqlcmd -S <server> -C -v DatabaseName="BlueTrack" -i 07_BlueTrack_SqlServerAudit.sql

   Windows must allow it first (Microsoft, "Write SQL Server Audit events to
   the Security log"): the audit policy subcategory Application Generated
   on for success and failure, the SQL Server service account holding the
   "Generate security audits" right (a SQL Server restart after granting
   it), and that account allowed full control of the registry key
   HKLM\SYSTEM\CurrentControlSet\Services\EventLog\Security. Without them
   the audit is created but can't start or write; check it with
       SELECT name, status_desc, status_time FROM sys.dm_server_audit_status;

   Creates (or recreates, so it is safe to re-run):
     - the server audit "BlueTrack ($(DatabaseName)) - Audit Log Changes",
       TO SECURITY_LOG, ON_FAILURE = CONTINUE (BlueTrack keeps working if
       Windows refuses the events; the status above shows it), and
     - in the database, the audit specification "BlueTrack Audit Log
       Changes": UPDATE and DELETE on web.audit_event, web.audit_field_change,
       web.audit_chain, web.audit_chain_key and web.audit_chain_purged, by
       public (everyone), plus changes to those objects' definitions and
       permissions.
   INSERT isn't audited: BlueTrack inserts audit rows all day, and the seals
   cover what is inserted. The retention purge's deletes (the SQL Agent job,
   05) are recorded too, once a night.

   The names embed the database name: server audits are instance-wide, so
   BlueTrack and BlueTrackTest on one instance get their own.
   ============================================================================ */

USE master;
GO

DECLARE @AuditName SYSNAME = N'BlueTrack ($(DatabaseName)) - Audit Log Changes';

-- The database specification refers to the server audit; it goes first.
DECLARE @sql NVARCHAR(MAX) = N'
USE ' + QUOTENAME(N'$(DatabaseName)') + N';
IF EXISTS (SELECT 1 FROM sys.database_audit_specifications WHERE name = N''BlueTrack Audit Log Changes'')
BEGIN
    ALTER DATABASE AUDIT SPECIFICATION [BlueTrack Audit Log Changes] WITH (STATE = OFF);
    DROP DATABASE AUDIT SPECIFICATION [BlueTrack Audit Log Changes];
END';
EXEC (@sql);

IF EXISTS (SELECT 1 FROM sys.server_audits WHERE name = @AuditName)
BEGIN
    SET @sql = N'ALTER SERVER AUDIT ' + QUOTENAME(@AuditName) + N' WITH (STATE = OFF); DROP SERVER AUDIT ' + QUOTENAME(@AuditName) + N';';
    EXEC (@sql);
END

SET @sql = N'CREATE SERVER AUDIT ' + QUOTENAME(@AuditName) + N' TO SECURITY_LOG WITH (QUEUE_DELAY = 1000, ON_FAILURE = CONTINUE);';
EXEC (@sql);
GO

USE [$(DatabaseName)];
GO

DECLARE @AuditName SYSNAME = N'BlueTrack ($(DatabaseName)) - Audit Log Changes';
DECLARE @sql NVARCHAR(MAX) = N'
CREATE DATABASE AUDIT SPECIFICATION [BlueTrack Audit Log Changes]
FOR SERVER AUDIT ' + QUOTENAME(@AuditName) + N'
    ADD (UPDATE, DELETE ON OBJECT::web.audit_event BY public),
    ADD (UPDATE, DELETE ON OBJECT::web.audit_field_change BY public),
    ADD (UPDATE, DELETE ON OBJECT::web.audit_chain BY public),
    ADD (UPDATE, DELETE ON OBJECT::web.audit_chain_key BY public),
    ADD (UPDATE, DELETE ON OBJECT::web.audit_chain_purged BY public),
    ADD (SCHEMA_OBJECT_CHANGE_GROUP),
    ADD (SCHEMA_OBJECT_PERMISSION_CHANGE_GROUP)
    WITH (STATE = ON);';
EXEC (@sql);
GO

USE master;
GO

-- Starting it is where Windows' permissions matter: with ON_FAILURE = CONTINUE a refusal is shown on
-- sys.dm_server_audit_status rather than failing the script.
DECLARE @AuditName SYSNAME = N'BlueTrack ($(DatabaseName)) - Audit Log Changes';
DECLARE @sql NVARCHAR(MAX) = N'ALTER SERVER AUDIT ' + QUOTENAME(@AuditName) + N' WITH (STATE = ON);';
BEGIN TRY
    EXEC (@sql);
END TRY
BEGIN CATCH
    PRINT 'WARNING: the server audit could not start: ' + ERROR_MESSAGE();
END CATCH

SELECT name, status_desc, status_time FROM sys.dm_server_audit_status WHERE name = @AuditName;
GO

PRINT '07_BlueTrack_SqlServerAudit.sql complete.';
GO
