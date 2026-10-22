/* ============================================================================
   54_BlueTrack_AuditChain.sql

   RUN THIS AFTER 01-53. Guarded -- safe to re-run.

   D-295 (the user's choices of 2026-10-21, reversing D-63): a tamper-evident
   audit log. The API seals each audit event, in WriteVersion order, with an
   HMAC-SHA256 over the event (and its field changes) and the seal before it,
   using a key only the API can read (DPAPI under the app pool account). An
   event or a seal edited or deleted afterwards breaks the chain from that
   point; Verify on the Audit Log page, and the AuditChainBroken notification,
   say where.

     web.audit_chain          one seal per audit event: ChainSeq (1, 2, 3 ...
                              with no gaps), the event, the key it was made
                              with, the hash, when. The app's account may
                              insert and read, not change or delete
                              (Database/Manual/02).
     web.audit_chain_key      the HMAC keys, protected by the API (never the
                              key itself in plain text).
     web.audit_chain_state    one row: the WriteVersion every event at or
                              below has been looked at, the last seal, the
                              last Verify.
     web.audit_chain_purged   each run of seals the retention purge deleted
                              (first and last ChainSeq, the last one's hash),
                              so Verify carries on across it; a gap with no
                              such row is reported.
     dbo.usp_PurgeAuditLog    redefined (as in 26, plus): deletes only sealed
                              events, with their seals, and records the runs
                              it deleted. An event not sealed yet is kept for
                              the next run.
     AuditChainVerified       audit event type: a Verify ran, and its result.
     AuditChainBroken         notification type (off): the daily Verify found
                              a break.

   Events that exist when this runs are sealed by the API in the background,
   oldest first.
   ============================================================================ */

USE $DatabaseName$;
GO

IF OBJECT_ID('web.audit_chain_key') IS NULL
    CREATE TABLE web.audit_chain_key (
        AuditChainKeyId   INT IDENTITY(1,1) NOT NULL PRIMARY KEY,
        ProtectedKey      NVARCHAR(2000)    NOT NULL,
        CreatedAt         DATETIME2         NOT NULL CONSTRAINT DF_audit_chain_key_CreatedAt DEFAULT SYSUTCDATETIME()
    );
GO

IF OBJECT_ID('web.audit_chain') IS NULL
    CREATE TABLE web.audit_chain (
        ChainSeq          BIGINT            NOT NULL PRIMARY KEY,
        AuditEventKey     BIGINT            NOT NULL CONSTRAINT UQ_audit_chain_AuditEventKey UNIQUE,
        AuditChainKeyId   INT               NOT NULL REFERENCES web.audit_chain_key(AuditChainKeyId),
        ChainHash         VARBINARY(32)     NOT NULL,
        SealedAt          DATETIME2         NOT NULL CONSTRAINT DF_audit_chain_SealedAt DEFAULT SYSUTCDATETIME()
    );
GO

IF OBJECT_ID('web.audit_chain_state') IS NULL
BEGIN
    CREATE TABLE web.audit_chain_state (
        AuditChainStateKey  INT               NOT NULL PRIMARY KEY CONSTRAINT CK_audit_chain_state_Single CHECK (AuditChainStateKey = 1),
        LookedAtVersion     BINARY(8)         NOT NULL CONSTRAINT DF_audit_chain_state_LookedAtVersion DEFAULT 0x0000000000000000,
        LastChainSeq        BIGINT            NOT NULL CONSTRAINT DF_audit_chain_state_LastChainSeq DEFAULT 0,
        LastChainHash       VARBINARY(32)     NULL,
        LastSealedAt        DATETIME2         NULL,
        LastVerifiedAt      DATETIME2         NULL,
        LastVerifyIntact    BIT               NULL,
        LastVerifySummary   NVARCHAR(1000)    NULL
    );
    INSERT INTO web.audit_chain_state (AuditChainStateKey) VALUES (1);
END
GO

IF OBJECT_ID('web.audit_chain_purged') IS NULL
    CREATE TABLE web.audit_chain_purged (
        FromChainSeq      BIGINT            NOT NULL PRIMARY KEY,
        ToChainSeq        BIGINT            NOT NULL,
        ToChainHash       VARBINARY(32)     NOT NULL,
        PurgeBatchId      UNIQUEIDENTIFIER  NOT NULL
    );
GO

INSERT INTO web.dim_audit_event_type (EventTypeName, Description)
SELECT 'AuditChainVerified', 'The audit log''s seals were checked: intact, or where the chain breaks (D-295)'
WHERE NOT EXISTS (SELECT 1 FROM web.dim_audit_event_type WHERE EventTypeName = 'AuditChainVerified');
GO

INSERT INTO web.dim_notification_type (NotificationTypeName, Description, IsEnabled)
SELECT 'AuditChainBroken', 'Once a day, from the daily digest time: the audit log''s seals are checked, and this is sent when an audit event or seal was changed or deleted (D-295).', 0
WHERE NOT EXISTS (SELECT 1 FROM web.dim_notification_type WHERE NotificationTypeName = 'AuditChainBroken');
GO

-- As in 26, plus D-295: only sealed events are deleted (one not sealed yet,
-- which the API seals within seconds, waits for the next run), with their
-- seals; each run of consecutive seals deleted is recorded in
-- web.audit_chain_purged with the hash of its last seal, which the next seal
-- was made after, so Verify carries on across it.
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
        BEGIN TRANSACTION;

        DECLARE @Purge TABLE (AuditEventKey BIGINT PRIMARY KEY, ChainSeq BIGINT NOT NULL UNIQUE);
        INSERT INTO @Purge (AuditEventKey, ChainSeq)
        SELECT ae.AuditEventKey, c.ChainSeq
        FROM web.audit_event ae
        JOIN web.audit_chain c ON c.AuditEventKey = ae.AuditEventKey
        WHERE ae.OccurredAt < @CutoffDate
          AND (@KeepAbove IS NULL OR ae.WriteVersion <= @KeepAbove);

        -- Runs of consecutive ChainSeq (gaps and islands), each with its last seal's hash.
        INSERT INTO web.audit_chain_purged (FromChainSeq, ToChainSeq, ToChainHash, PurgeBatchId)
        SELECT r.FromSeq, r.ToSeq, c.ChainHash, @PurgeBatchId
        FROM (
            SELECT MIN(ChainSeq) AS FromSeq, MAX(ChainSeq) AS ToSeq
            FROM (SELECT ChainSeq, ChainSeq - ROW_NUMBER() OVER (ORDER BY ChainSeq) AS Island FROM @Purge) i
            GROUP BY Island
        ) r
        JOIN web.audit_chain c ON c.ChainSeq = r.ToSeq;

        DELETE fc FROM web.audit_field_change fc JOIN @Purge p ON p.AuditEventKey = fc.AuditEventKey;
        DELETE ae FROM web.audit_event ae JOIN @Purge p ON p.AuditEventKey = ae.AuditEventKey;
        DECLARE @RowsPurged INT = @@ROWCOUNT;
        DELETE c FROM web.audit_chain c JOIN @Purge p ON p.ChainSeq = c.ChainSeq;

        INSERT INTO web.audit_purge_log (PurgeBatchId, CutoffDate, RowsPurged, StartedAt, CompletedAt, Status, ErrorMessage)
        VALUES (@PurgeBatchId, @CutoffDate, @RowsPurged, @StartedAt, SYSUTCDATETIME(), 'Succeeded', NULL);

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
        INSERT INTO web.audit_purge_log (PurgeBatchId, CutoffDate, RowsPurged, StartedAt, CompletedAt, Status, ErrorMessage)
        VALUES (@PurgeBatchId, @CutoffDate, NULL, @StartedAt, SYSUTCDATETIME(), 'Failed', ERROR_MESSAGE());
        THROW;
    END CATCH
END
GO

PRINT '54_BlueTrack_AuditChain.sql complete.';
GO
