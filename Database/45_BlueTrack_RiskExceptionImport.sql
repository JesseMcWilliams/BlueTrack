/* ============================================================================
   45_BlueTrack_RiskExceptionImport.sql

   RUN THIS AFTER 01-44. Guarded (COL_LENGTH / sys.indexes / sys.check_
   constraints checks) -- safe to re-run, per D-58's incremental-script
   convention.

   D-183: Risk Exceptions bulk import, for exceptions approved in another
   tool. Each keeps a BlueTrack ExceptionID (the configured pattern) plus
   where it came from; its approver is usually not a BlueTrack user.
     ApprovedBy            now NULL-able: an imported exception names its
                           approver as text (ApprovedByName) instead.
                           CK_risk_exception_Approver keeps one of the two set.
     ApprovedByName        the approver's name in the source tool.
     SourceTool            e.g. 'ServiceNow GRC'.
     SourceExceptionId     the exception's ID in that tool; unique per tool
                           (UX_risk_exception_Source), which is how a repeat
                           import is reported as an error.
     SourceUrl             a link to it in that tool.
     ImportedBy/ImportedDate   who imported it, and when.
     'ExceptionImported'   audit event type, one per imported exception.
   ============================================================================ */

USE $DatabaseName$;
GO

IF EXISTS (SELECT 1 FROM sys.columns WHERE object_id = OBJECT_ID('web.risk_exception') AND name = 'ApprovedBy' AND is_nullable = 0)
    ALTER TABLE web.risk_exception ALTER COLUMN ApprovedBy INT NULL;
GO

IF COL_LENGTH('web.risk_exception', 'ApprovedByName') IS NULL
    ALTER TABLE web.risk_exception ADD ApprovedByName NVARCHAR(200) NULL;
GO
IF COL_LENGTH('web.risk_exception', 'SourceTool') IS NULL
    ALTER TABLE web.risk_exception ADD SourceTool NVARCHAR(100) NULL;
GO
IF COL_LENGTH('web.risk_exception', 'SourceExceptionId') IS NULL
    ALTER TABLE web.risk_exception ADD SourceExceptionId NVARCHAR(100) NULL;
GO
IF COL_LENGTH('web.risk_exception', 'SourceUrl') IS NULL
    ALTER TABLE web.risk_exception ADD SourceUrl NVARCHAR(1000) NULL;
GO
IF COL_LENGTH('web.risk_exception', 'ImportedBy') IS NULL
    ALTER TABLE web.risk_exception ADD ImportedBy INT NULL CONSTRAINT FK_risk_exception_ImportedBy REFERENCES web.app_user(UserKey);
GO
IF COL_LENGTH('web.risk_exception', 'ImportedDate') IS NULL
    ALTER TABLE web.risk_exception ADD ImportedDate DATETIME2 NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_risk_exception_Approver')
    ALTER TABLE web.risk_exception ADD CONSTRAINT CK_risk_exception_Approver CHECK (ApprovedBy IS NOT NULL OR ApprovedByName IS NOT NULL);
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('web.risk_exception') AND name = 'UX_risk_exception_Source')
    CREATE UNIQUE INDEX UX_risk_exception_Source ON web.risk_exception (SourceTool, SourceExceptionId)
    WHERE SourceExceptionId IS NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM web.dim_audit_event_type WHERE EventTypeName = 'ExceptionImported')
    INSERT INTO web.dim_audit_event_type (EventTypeName, Description)
    VALUES ('ExceptionImported', 'A risk exception approved in another tool was imported');
GO

PRINT '45_BlueTrack_RiskExceptionImport.sql complete.';
