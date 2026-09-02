using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the Audit Log Viewer's search/filter (by user, date range, event
/// type, entity, per Design_Audit_Logging.md's Admin UI Requirements) and
/// its field-level drill-down.
/// </summary>
public sealed class AuditRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<AuditEventSummary>> GetEventsAsync(
        string? eventTypeName, string? entityName, int? performedByUserKey, DateTime? fromDate, DateTime? toDate)
    {
        using var connection = connectionFactory.Create();

        const string sql = """
            SELECT
                ae.AuditEventKey,
                aet.EventTypeName,
                ae.OccurredAt,
                au.DisplayName AS PerformedByName,
                ae.EntityName,
                ae.EntityKey,
                ae.SourceIpAddress,
                ae.Detail,
                ae.Reason
            FROM web.audit_event ae
            JOIN web.dim_audit_event_type aet ON aet.AuditEventTypeKey = ae.AuditEventTypeKey
            LEFT JOIN web.app_user au          ON au.UserKey = ae.PerformedByUserKey
            WHERE (@EventTypeName IS NULL OR aet.EventTypeName = @EventTypeName)
              AND (@EntityName IS NULL OR ae.EntityName = @EntityName)
              AND (@PerformedByUserKey IS NULL OR ae.PerformedByUserKey = @PerformedByUserKey)
              AND (@FromDate IS NULL OR ae.OccurredAt >= @FromDate)
              AND (@ToDate IS NULL OR ae.OccurredAt < DATEADD(DAY, 1, @ToDate))
            ORDER BY ae.OccurredAt DESC
            """;

        var rows = await connection.QueryAsync<AuditEventSummary>(sql, new
        {
            EventTypeName = eventTypeName,
            EntityName = entityName,
            PerformedByUserKey = performedByUserKey,
            FromDate = fromDate,
            ToDate = toDate
        });
        return rows.AsList();
    }

    public async Task<IReadOnlyList<AuditFieldChangeSummary>> GetFieldChangesAsync(long auditEventKey)
    {
        using var connection = connectionFactory.Create();

        const string sql = """
            SELECT FieldName, OldValue, NewValue
            FROM web.audit_field_change
            WHERE AuditEventKey = @AuditEventKey
            ORDER BY AuditFieldChangeKey
            """;

        var rows = await connection.QueryAsync<AuditFieldChangeSummary>(sql, new { AuditEventKey = auditEventKey });
        return rows.AsList();
    }
}
