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
    private static readonly IReadOnlyDictionary<string, string> SortableColumns = new Dictionary<string, string>
    {
        ["occurredAt"] = "ae.OccurredAt",
        ["eventTypeName"] = "aet.EventTypeName",
        ["performedByName"] = "au.DisplayName",
        ["entityName"] = "ae.EntityName"
    };

    /// <summary>D-42: adds multi-column sort on top of the existing stacked filters (event type/entity/user/date range).</summary>
    public async Task<IReadOnlyList<AuditEventSummary>> GetEventsAsync(
        string? eventTypeName, string? entityName, int? performedByUserKey, DateTime? fromDate, DateTime? toDate,
        IReadOnlyList<(string Field, bool Descending)>? sortBy = null)
    {
        using var connection = connectionFactory.Create();

        var sql = $"""
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
            ORDER BY {BuildOrderByClause(sortBy)}
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

    private static string BuildOrderByClause(IReadOnlyList<(string Field, bool Descending)>? sortBy)
    {
        if (sortBy is not { Count: > 0 })
        {
            return "ae.OccurredAt DESC";
        }

        var clauses = sortBy
            .Where(s => SortableColumns.ContainsKey(s.Field))
            .Select(s => $"{SortableColumns[s.Field]} {(s.Descending ? "DESC" : "ASC")}")
            .ToList();

        return clauses.Count > 0 ? string.Join(", ", clauses) : "ae.OccurredAt DESC";
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
