using Dapper;
using BlueTrack.Api.Siem;

namespace BlueTrack.Api.Data;

/// <summary>
/// D-234 (26_BlueTrack_SiemForwarding.sql): the SIEM forwarding settings, the per-type list, the forwarder's
/// state row (watermark, lease, last send) and the audit rows waiting to be sent.
///
/// Rows are read in audit_event.WriteVersion (ROWVERSION) order and only below MIN_ACTIVE_ROWVERSION(): a
/// row whose transaction is still open has a WriteVersion at or above that value, so nothing below it can
/// still appear, and the watermark (the WriteVersion of the last row handled) never passes a row that
/// commits late. An IDENTITY watermark would: key 10 can commit after key 11 was read.
/// </summary>
public sealed class SiemForwardingRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<SiemForwardingSettings> GetSettingsAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<SiemForwardingSettings>("""
            SELECT c.IsEnabled, c.Host, c.Port, c.Transport, c.Framing, CAST(c.Facility AS INT) AS Facility, c.SyslogHostName, c.AppName,
                   c.TimeoutSeconds, c.MaxMessageBytes, c.TlsTrustMode, c.TlsFingerprint, c.TlsCaCertificate, c.TlsCheckRevocation,
                   c.ModifiedDate, u.DisplayName AS ModifiedByName
            FROM web.siem_forwarding_config c
            LEFT JOIN web.app_user u ON u.UserKey = c.ModifiedBy
            WHERE c.SiemForwardingConfigKey = 1
            """) ?? new SiemForwardingSettings();
    }

    /// <summary>
    /// Saves the settings and, if given, the per-type list. Turning forwarding on (off before) starts after
    /// everything that exists now: the watermark becomes MIN_ACTIVE_ROWVERSION() - 1, so rows already
    /// committed are skipped and rows still being written are sent. The error and failure count reset.
    /// </summary>
    public async Task SaveAsync(SiemForwardingSettings settings, IReadOnlyList<SaveSiemEventTypeSetting>? eventTypes, int userKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("""
            DECLARE @WasEnabled BIT = (SELECT IsEnabled FROM web.siem_forwarding_config WITH (UPDLOCK, HOLDLOCK) WHERE SiemForwardingConfigKey = 1);

            UPDATE web.siem_forwarding_config SET
                IsEnabled = @IsEnabled, Host = @Host, Port = @Port, Transport = @Transport, Framing = @Framing, Facility = @Facility,
                SyslogHostName = @SyslogHostName, AppName = @AppName, TimeoutSeconds = @TimeoutSeconds, MaxMessageBytes = @MaxMessageBytes,
                TlsTrustMode = @TlsTrustMode, TlsFingerprint = @TlsFingerprint, TlsCaCertificate = @TlsCaCertificate,
                TlsCheckRevocation = @TlsCheckRevocation, ModifiedBy = @userKey, ModifiedDate = SYSUTCDATETIME()
            WHERE SiemForwardingConfigKey = 1;

            IF @IsEnabled = 1 AND ISNULL(@WasEnabled, 0) = 0
                UPDATE web.siem_forwarding_state SET
                    Watermark = CAST(MIN_ACTIVE_ROWVERSION() AS BIGINT) - 1,
                    EnabledAt = SYSUTCDATETIME(),
                    EnabledFromAuditEventKey = (SELECT MAX(AuditEventKey) FROM web.audit_event),
                    LastError = NULL, LastErrorAt = NULL, ConsecutiveFailures = 0
                WHERE SiemForwardingStateKey = 1;
            """, new
        {
            settings.IsEnabled, settings.Host, settings.Port, settings.Transport, settings.Framing, settings.Facility, settings.SyslogHostName,
            settings.AppName, settings.TimeoutSeconds, settings.MaxMessageBytes, settings.TlsTrustMode, settings.TlsFingerprint,
            settings.TlsCaCertificate, settings.TlsCheckRevocation, userKey
        }, transaction);

        foreach (var type in eventTypes ?? [])
        {
            await connection.ExecuteAsync("""
                MERGE web.siem_event_type_setting AS tgt
                USING (SELECT AuditEventTypeKey FROM web.dim_audit_event_type WHERE AuditEventTypeKey = @AuditEventTypeKey) AS src
                    ON tgt.AuditEventTypeKey = src.AuditEventTypeKey
                WHEN MATCHED AND (tgt.IsForwarded <> @IsForwarded OR tgt.CefSeverity <> @CefSeverity) THEN
                    UPDATE SET IsForwarded = @IsForwarded, CefSeverity = @CefSeverity, ModifiedBy = @userKey, ModifiedDate = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN
                    INSERT (AuditEventTypeKey, IsForwarded, CefSeverity, ModifiedBy, ModifiedDate)
                    VALUES (src.AuditEventTypeKey, @IsForwarded, @CefSeverity, @userKey, SYSUTCDATETIME());
                """, new { type.AuditEventTypeKey, type.IsForwarded, type.CefSeverity, userKey }, transaction);
        }
        transaction.Commit();
    }

    /// <summary>Every audit event type with its forwarding setting (a type with no row: forwarded, severity 5).</summary>
    public async Task<IReadOnlyList<SiemEventTypeSetting>> GetEventTypesAsync()
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<SiemEventTypeSetting>("""
            SELECT t.AuditEventTypeKey, t.EventTypeName, t.Description,
                   ISNULL(s.IsForwarded, CAST(1 AS BIT)) AS IsForwarded, CAST(ISNULL(s.CefSeverity, 5) AS INT) AS CefSeverity
            FROM web.dim_audit_event_type t
            LEFT JOIN web.siem_event_type_setting s ON s.AuditEventTypeKey = t.AuditEventTypeKey
            ORDER BY t.EventTypeName
            """)).AsList();
    }

    /// <summary>
    /// The state row plus what is waiting: rows of forwarded types above the watermark. Read uncommitted, so a
    /// status page or health check never waits on an open audit write (an approximate count is fine here).
    /// </summary>
    public async Task<SiemForwardingStatus> GetStatusAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<SiemForwardingStatus>("""
            SELECT s.Watermark, s.EnabledAt, s.EnabledFromAuditEventKey, s.LastAttemptAt, s.LastSuccessAt, s.LastSentAuditEventKey,
                   s.EventsSent, s.LastError, s.LastErrorAt, s.ConsecutiveFailures, s.LeaseOwner, s.LeaseExpiresAt,
                   ISNULL(p.PendingCount, 0) AS PendingCount, p.OldestPendingAt
            FROM web.siem_forwarding_state s
            OUTER APPLY (
                SELECT COUNT(*) AS PendingCount, MIN(ae.OccurredAt) AS OldestPendingAt
                FROM web.audit_event ae WITH (NOLOCK)
                LEFT JOIN web.siem_event_type_setting ts ON ts.AuditEventTypeKey = ae.AuditEventTypeKey
                WHERE s.Watermark IS NOT NULL
                  AND ae.WriteVersion > CAST(s.Watermark AS BINARY(8))
                  AND ISNULL(ts.IsForwarded, 1) = 1
            ) p
            WHERE s.SiemForwardingStateKey = 1
            """);
    }

    /// <summary>
    /// Takes or renews the single-row lease: free, already ours, or expired. True if this owner now holds it.
    /// Only the holder sends, so two app pool workers (or an overlapped recycle) never both send.
    /// </summary>
    public async Task<bool> TryAcquireLeaseAsync(string owner, TimeSpan duration)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync("""
            UPDATE web.siem_forwarding_state
            SET LeaseOwner = @owner, LeaseExpiresAt = DATEADD(MILLISECOND, @ms, SYSUTCDATETIME())
            WHERE SiemForwardingStateKey = 1
              AND (LeaseOwner IS NULL OR LeaseOwner = @owner OR LeaseExpiresAt IS NULL OR LeaseExpiresAt < SYSUTCDATETIME())
            """, new { owner, ms = (int)duration.TotalMilliseconds }) == 1;
    }

    public async Task ReleaseLeaseAsync(string owner)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(
            "UPDATE web.siem_forwarding_state SET LeaseOwner = NULL, LeaseExpiresAt = NULL WHERE SiemForwardingStateKey = 1 AND LeaseOwner = @owner",
            new { owner });
    }

    public async Task<long?> GetStatusWatermarkAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<long?>("SELECT Watermark FROM web.siem_forwarding_state WHERE SiemForwardingStateKey = 1");
    }

    /// <summary>Sets the watermark to now (no backfill) when forwarding is on but none was ever set.</summary>
    public async Task<long> EnsureWatermarkAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<long>("""
            UPDATE web.siem_forwarding_state
            SET Watermark = ISNULL(Watermark, CAST(MIN_ACTIVE_ROWVERSION() AS BIGINT) - 1),
                EnabledAt = ISNULL(EnabledAt, SYSUTCDATETIME()),
                EnabledFromAuditEventKey = ISNULL(EnabledFromAuditEventKey, (SELECT MAX(AuditEventKey) FROM web.audit_event))
            OUTPUT inserted.Watermark
            WHERE SiemForwardingStateKey = 1
            """);
    }

    /// <summary>
    /// The next rows after the watermark, in WriteVersion order, only below MIN_ACTIVE_ROWVERSION() (every row
    /// there is committed), with their type's setting and field changes. Includes types that aren't forwarded,
    /// so the watermark can move past them. LOCK_TIMEOUT: a read that would wait more than 5 s fails and is retried.
    /// </summary>
    public async Task<IReadOnlyList<SiemAuditRow>> ReadBatchAsync(long watermark, int batchSize)
    {
        using var connection = connectionFactory.Create();
        var rows = (await connection.QueryAsync<SiemAuditRow>("""
            SET LOCK_TIMEOUT 5000;
            DECLARE @Below BINARY(8) = MIN_ACTIVE_ROWVERSION();
            SELECT TOP (@batchSize)
                ae.AuditEventKey, CAST(ae.WriteVersion AS BIGINT) AS WriteVersion, ae.OccurredAt, t.EventTypeName,
                t.Description AS EventTypeDescription, u.DisplayName AS UserDisplayName, u.ExternalIdentifier AS UserExternalIdentifier,
                ae.EntityName, ae.EntityKey, ae.SourceIpAddress, ae.Detail, ae.Reason,
                ISNULL(s.IsForwarded, CAST(1 AS BIT)) AS IsForwarded, CAST(ISNULL(s.CefSeverity, 5) AS INT) AS CefSeverity
            FROM web.audit_event ae
            JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = ae.AuditEventTypeKey
            LEFT JOIN web.app_user u ON u.UserKey = ae.PerformedByUserKey
            LEFT JOIN web.siem_event_type_setting s ON s.AuditEventTypeKey = ae.AuditEventTypeKey
            WHERE ae.WriteVersion > CAST(@watermark AS BINARY(8)) AND ae.WriteVersion < @Below
            ORDER BY ae.WriteVersion
            """, new { watermark, batchSize })).AsList();

        var forwarded = rows.Where(r => r.IsForwarded).Select(r => r.AuditEventKey).ToList();
        if (forwarded.Count > 0)
        {
            var changes = await connection.QueryAsync<FieldChangeRow>("""
                SET LOCK_TIMEOUT 5000;
                SELECT AuditEventKey, FieldName, OldValue, NewValue FROM web.audit_field_change
                WHERE AuditEventKey IN @forwarded ORDER BY AuditFieldChangeKey
                """, new { forwarded });
            var byEvent = changes.GroupBy(c => c.AuditEventKey).ToDictionary(g => g.Key, g => g.Select(c => (c.FieldName, c.OldValue, c.NewValue)).ToList());
            foreach (var row in rows)
            {
                if (byEvent.TryGetValue(row.AuditEventKey, out var list)) row.FieldChanges = list;
            }
        }
        return rows;
    }

    /// <summary>
    /// Records a delivered (or skipped) batch: moves the watermark from what this sender read to the batch's
    /// last WriteVersion. Only while this owner still holds an unexpired lease and the watermark is unchanged;
    /// false otherwise (the lease was lost, so another sender may resend these rows -- never skip them).
    /// </summary>
    public async Task<bool> AdvanceAsync(string owner, long fromWatermark, long toWatermark, int sent, long? lastSentKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync("""
            UPDATE web.siem_forwarding_state SET
                Watermark = @toWatermark,
                EventsSent = EventsSent + @sent,
                LastSentAuditEventKey = ISNULL(@lastSentKey, LastSentAuditEventKey),
                LastAttemptAt = CASE WHEN @sent > 0 THEN SYSUTCDATETIME() ELSE LastAttemptAt END,
                LastSuccessAt = CASE WHEN @sent > 0 THEN SYSUTCDATETIME() ELSE LastSuccessAt END,
                LastError = CASE WHEN @sent > 0 THEN NULL ELSE LastError END,
                ConsecutiveFailures = CASE WHEN @sent > 0 THEN 0 ELSE ConsecutiveFailures END
            WHERE SiemForwardingStateKey = 1 AND LeaseOwner = @owner AND LeaseExpiresAt > SYSUTCDATETIME() AND Watermark = @fromWatermark
            """, new { owner, fromWatermark, toWatermark, sent, lastSentKey }) == 1;
    }

    public async Task RecordFailureAsync(string owner, string error)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("""
            UPDATE web.siem_forwarding_state SET
                LastAttemptAt = SYSUTCDATETIME(), LastError = @error, LastErrorAt = SYSUTCDATETIME(), ConsecutiveFailures = ConsecutiveFailures + 1
            WHERE SiemForwardingStateKey = 1 AND LeaseOwner = @owner
            """, new { owner, error = error.Length <= 2000 ? error : error[..1997] + "..." });
    }
}

internal sealed class FieldChangeRow
{
    public long AuditEventKey { get; set; }
    public string FieldName { get; set; } = "";
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}

/// <summary>One audit row as the forwarder reads it.</summary>
public sealed class SiemAuditRow
{
    public long AuditEventKey { get; set; }
    public long WriteVersion { get; set; }
    public DateTime OccurredAt { get; set; }
    public string EventTypeName { get; set; } = "";
    public string? EventTypeDescription { get; set; }
    public string? UserDisplayName { get; set; }
    public string? UserExternalIdentifier { get; set; }
    public string? EntityName { get; set; }
    public string? EntityKey { get; set; }
    public string? SourceIpAddress { get; set; }
    public string? Detail { get; set; }
    public string? Reason { get; set; }
    public bool IsForwarded { get; set; }
    public int CefSeverity { get; set; }
    public IReadOnlyList<(string FieldName, string? OldValue, string? NewValue)> FieldChanges { get; set; } = [];
}
