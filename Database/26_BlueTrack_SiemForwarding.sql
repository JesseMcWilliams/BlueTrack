/* ============================================================================
   26_BlueTrack_SiemForwarding.sql

   RUN THIS AFTER 01-25. Guarded -- safe to re-run.

   D-234: forward the audit log to a SIEM over syslog (CEF inside RFC 5424,
   over TCP or TLS), decided by the user on 2026-10-14. Configured on
   Admin > SIEM Forwarding; off until an admin turns it on. Nothing already
   in the audit log is sent: turning forwarding on starts after the rows
   that exist then.

     web.audit_event.WriteVersion       a ROWVERSION column (and its index).
                                         The sender reads rows in WriteVersion
                                         order and only below
                                         MIN_ACTIVE_ROWVERSION(), so a row whose
                                         transaction commits after a later
                                         one was read is never skipped (an
                                         IDENTITY key watermark could skip it).
                                         Adding it fills every existing row,
                                         once.
     web.siem_forwarding_config         one row: on/off, collector host and
                                         port, TCP or TLS, framing, facility,
                                         syslog HOSTNAME/APP-NAME, timeout,
                                         largest message (bytes),
                                         and how TLS trusts the collector
                                         (system store, a pinned certificate
                                         fingerprint, or a given CA).
     web.siem_event_type_setting        per audit event type: forwarded or
                                         not, and its CEF severity (0-10).
                                         Seeded for every type that exists;
                                         a type with no row is forwarded at
                                         severity 5.
     web.siem_forwarding_state          one row: the watermark (every row at
                                         or below it is sent or skipped),
                                         the last send and error, and the
                                         lease that lets only one API process
                                         send.
     dbo.usp_PurgeAuditLog              redefined: while forwarding is on it
                                         keeps rows not yet forwarded, however
                                         old (and their field changes).
   ============================================================================ */

USE $DatabaseName$;
GO

IF COL_LENGTH('web.audit_event', 'WriteVersion') IS NULL
    ALTER TABLE web.audit_event ADD WriteVersion ROWVERSION NOT NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_audit_event_WriteVersion' AND object_id = OBJECT_ID('web.audit_event'))
    CREATE UNIQUE INDEX IX_audit_event_WriteVersion ON web.audit_event (WriteVersion) INCLUDE (AuditEventTypeKey, OccurredAt);
GO

-- The sender reads a batch's field changes by AuditEventKey; without this index that read scans the table and
-- waits on the field changes of an audit transaction that is still open.
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_audit_field_change_AuditEventKey' AND object_id = OBJECT_ID('web.audit_field_change'))
    CREATE INDEX IX_audit_field_change_AuditEventKey ON web.audit_field_change (AuditEventKey);
GO

IF OBJECT_ID('web.siem_forwarding_config') IS NULL
BEGIN
    CREATE TABLE web.siem_forwarding_config (
        SiemForwardingConfigKey INT NOT NULL CONSTRAINT PK_siem_forwarding_config PRIMARY KEY
            CONSTRAINT CK_siem_forwarding_config_SingleRow CHECK (SiemForwardingConfigKey = 1),
        IsEnabled           BIT            NOT NULL CONSTRAINT DF_siem_forwarding_config_IsEnabled DEFAULT 0,
        Host                NVARCHAR(255)  NULL,
        Port                INT            NOT NULL CONSTRAINT DF_siem_forwarding_config_Port DEFAULT 6514
            CONSTRAINT CK_siem_forwarding_config_Port CHECK (Port BETWEEN 1 AND 65535),
        Transport           NVARCHAR(10)   NOT NULL CONSTRAINT DF_siem_forwarding_config_Transport DEFAULT 'Tls'
            CONSTRAINT CK_siem_forwarding_config_Transport CHECK (Transport IN ('Tcp', 'Tls')),
        Framing             NVARCHAR(20)   NOT NULL CONSTRAINT DF_siem_forwarding_config_Framing DEFAULT 'OctetCounting'
            CONSTRAINT CK_siem_forwarding_config_Framing CHECK (Framing IN ('OctetCounting', 'LineFeed')),
        Facility            TINYINT        NOT NULL CONSTRAINT DF_siem_forwarding_config_Facility DEFAULT 20   -- local4
            CONSTRAINT CK_siem_forwarding_config_Facility CHECK (Facility BETWEEN 0 AND 23),
        SyslogHostName      NVARCHAR(255)  NULL,      -- NULL: this server's fully qualified name
        AppName             NVARCHAR(48)   NOT NULL CONSTRAINT DF_siem_forwarding_config_AppName DEFAULT 'BlueTrack',
        TimeoutSeconds      INT            NOT NULL CONSTRAINT DF_siem_forwarding_config_TimeoutSeconds DEFAULT 10
            CONSTRAINT CK_siem_forwarding_config_TimeoutSeconds CHECK (TimeoutSeconds BETWEEN 1 AND 120),
        MaxMessageBytes     INT            NOT NULL CONSTRAINT DF_siem_forwarding_config_MaxMessageBytes DEFAULT 8192   -- RFC 5425 4.3.1: receivers MUST take 2048, SHOULD take 8192
            CONSTRAINT CK_siem_forwarding_config_MaxMessageBytes CHECK (MaxMessageBytes BETWEEN 2048 AND 65535),
        TlsTrustMode        NVARCHAR(20)   NOT NULL CONSTRAINT DF_siem_forwarding_config_TlsTrustMode DEFAULT 'System'
            CONSTRAINT CK_siem_forwarding_config_TlsTrustMode CHECK (TlsTrustMode IN ('System', 'Fingerprint', 'CaCertificate')),
        TlsFingerprint      NVARCHAR(200)  NULL,      -- the collector certificate's SHA-256 or SHA-1 fingerprint (RFC 5425 4.2.2)
        TlsCaCertificate    NVARCHAR(MAX)  NULL,      -- PEM; the only trust anchor in CaCertificate mode
        TlsCheckRevocation  BIT            NOT NULL CONSTRAINT DF_siem_forwarding_config_TlsCheckRevocation DEFAULT 1,
        ModifiedBy          INT            NULL CONSTRAINT FK_siem_forwarding_config_ModifiedBy REFERENCES web.app_user(UserKey),
        ModifiedDate        DATETIME2      NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM web.siem_forwarding_config)
    INSERT INTO web.siem_forwarding_config (SiemForwardingConfigKey) VALUES (1);
GO

IF OBJECT_ID('web.siem_event_type_setting') IS NULL
BEGIN
    CREATE TABLE web.siem_event_type_setting (
        AuditEventTypeKey   INT       NOT NULL CONSTRAINT PK_siem_event_type_setting PRIMARY KEY
            CONSTRAINT FK_siem_event_type_setting_Type REFERENCES web.dim_audit_event_type(AuditEventTypeKey),
        IsForwarded         BIT       NOT NULL CONSTRAINT DF_siem_event_type_setting_IsForwarded DEFAULT 1,
        CefSeverity         TINYINT   NOT NULL CONSTRAINT DF_siem_event_type_setting_CefSeverity DEFAULT 5
            CONSTRAINT CK_siem_event_type_setting_CefSeverity CHECK (CefSeverity BETWEEN 0 AND 10),
        ModifiedBy          INT       NULL CONSTRAINT FK_siem_event_type_setting_ModifiedBy REFERENCES web.app_user(UserKey),
        ModifiedDate        DATETIME2 NULL
    );
END
GO

-- Default severities (CEF 0-10: 0-3 Low, 4-6 Medium, 7-8 High, 9-10 Very-High).
-- Routine edits 3; approvals, deletions, exports, Test buttons, sign-in and
-- rights configuration 5; break-glass sign-in 8. Editable on the page.
INSERT INTO web.siem_event_type_setting (AuditEventTypeKey, IsForwarded, CefSeverity)
SELECT t.AuditEventTypeKey, 1, d.CefSeverity
FROM web.dim_audit_event_type t
JOIN (VALUES
    ('Logon', 3), ('LogonFailed', 6), ('FieldEdit', 3), ('BulkEdit', 4), ('RecordViewed', 1),
    ('ExceptionApproved', 5), ('ExceptionReviewExtended', 5), ('ExceptionRevoked', 5), ('ExceptionImported', 5),
    ('ProviderConfigChanged', 5), ('ReloadRights', 5), ('AccountDeleted', 5), ('AccountUndeleted', 5),
    ('ConnectionTest', 5), ('ReportExport', 5), ('BreakGlassLogon', 8),
    ('EntitlementMemberResolution', 3), ('AccountClassification', 3)
) d (EventTypeName, CefSeverity) ON d.EventTypeName = t.EventTypeName
WHERE NOT EXISTS (SELECT 1 FROM web.siem_event_type_setting s WHERE s.AuditEventTypeKey = t.AuditEventTypeKey);
GO

IF OBJECT_ID('web.siem_forwarding_state') IS NULL
BEGIN
    CREATE TABLE web.siem_forwarding_state (
        SiemForwardingStateKey   INT            NOT NULL CONSTRAINT PK_siem_forwarding_state PRIMARY KEY
            CONSTRAINT CK_siem_forwarding_state_SingleRow CHECK (SiemForwardingStateKey = 1),
        Watermark                BIGINT         NULL,     -- audit_event.WriteVersion: every row at or below it is sent or skipped
        EnabledAt                DATETIME2      NULL,
        EnabledFromAuditEventKey BIGINT         NULL,     -- the highest AuditEventKey when forwarding was turned on (for display)
        LastAttemptAt            DATETIME2      NULL,
        LastSuccessAt            DATETIME2      NULL,
        LastSentAuditEventKey    BIGINT         NULL,
        EventsSent               BIGINT         NOT NULL CONSTRAINT DF_siem_forwarding_state_EventsSent DEFAULT 0,
        LastError                NVARCHAR(2000) NULL,     -- NULL once a send succeeds
        LastErrorAt              DATETIME2      NULL,
        ConsecutiveFailures      INT            NOT NULL CONSTRAINT DF_siem_forwarding_state_ConsecutiveFailures DEFAULT 0,
        LeaseOwner               NVARCHAR(200)  NULL,
        LeaseExpiresAt           DATETIME2      NULL
    );
END
GO

IF NOT EXISTS (SELECT 1 FROM web.siem_forwarding_state)
    INSERT INTO web.siem_forwarding_state (SiemForwardingStateKey) VALUES (1);
GO

-- D-234: while forwarding is on, a row above the watermark (not yet
-- forwarded) is kept however old it is, with its field changes; the next
-- run after it is forwarded deletes it. With forwarding off, unchanged.
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

    -- NULL = no forwarding limit. Forwarding on with no watermark yet keeps everything.
    DECLARE @KeepAbove BINARY(8) = NULL;
    IF EXISTS (SELECT 1 FROM web.siem_forwarding_config WHERE SiemForwardingConfigKey = 1 AND IsEnabled = 1)
        SET @KeepAbove = ISNULL((SELECT CAST(Watermark AS BINARY(8)) FROM web.siem_forwarding_state WHERE SiemForwardingStateKey = 1), 0x0000000000000000);

    BEGIN TRY
        DELETE fc
        FROM web.audit_field_change fc
        JOIN web.audit_event ae ON ae.AuditEventKey = fc.AuditEventKey
        WHERE ae.OccurredAt < @CutoffDate
          AND (@KeepAbove IS NULL OR ae.WriteVersion <= @KeepAbove);

        DELETE FROM web.audit_event
        WHERE OccurredAt < @CutoffDate
          AND (@KeepAbove IS NULL OR WriteVersion <= @KeepAbove);
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
