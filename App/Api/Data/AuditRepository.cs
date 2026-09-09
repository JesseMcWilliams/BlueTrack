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

    // D-124 Phase 3: shared between GetEventsAsync and GetFilteredCountAsync
    // so the filtered-count query mirrors the list query's own FROM/JOIN/WHERE
    // exactly -- the only real difference is paging/ORDER BY, which a COUNT
    // doesn't need.
    private const string FilterFromSql = """
        FROM web.audit_event ae
        JOIN web.dim_audit_event_type aet ON aet.AuditEventTypeKey = ae.AuditEventTypeKey
        LEFT JOIN web.app_user au          ON au.UserKey = ae.PerformedByUserKey
        WHERE (@EventTypeName IS NULL OR aet.EventTypeName = @EventTypeName)
          AND (@EntityName IS NULL OR ae.EntityName = @EntityName)
          AND (@PerformedByUserKey IS NULL OR ae.PerformedByUserKey = @PerformedByUserKey)
          AND (@FromDate IS NULL OR ae.OccurredAt >= @FromDate)
          AND (@ToDate IS NULL OR ae.OccurredAt < DATEADD(DAY, 1, @ToDate))
        """;

    /// <summary>D-42: adds multi-column sort on top of the existing stacked filters (event type/entity/user/date range). D-124 Phase 3: page/pageSize add SQL Server OFFSET/FETCH paging after the ORDER BY.</summary>
    public async Task<IReadOnlyList<AuditEventSummary>> GetEventsAsync(
        string? eventTypeName, string? entityName, int? performedByUserKey, DateTime? fromDate, DateTime? toDate,
        IReadOnlyList<(string Field, bool Descending)>? sortBy = null,
        int? page = null,
        int? pageSize = null)
    {
        using var connection = connectionFactory.Create();
        var (normalizedPage, normalizedPageSize) = PagingParams.Normalize(page, pageSize);

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
            {FilterFromSql}
            ORDER BY {BuildOrderByClause(sortBy)}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """;

        var rows = await connection.QueryAsync<AuditEventSummary>(sql, new
        {
            EventTypeName = eventTypeName,
            EntityName = entityName,
            PerformedByUserKey = performedByUserKey,
            FromDate = fromDate,
            ToDate = toDate,
            Offset = PagingParams.Offset(normalizedPage, normalizedPageSize),
            PageSize = normalizedPageSize
        });
        return rows.AsList();
    }

    /// <summary>D-121: the grand total row count with no filter applied -- backs the X-Total-Count response header on GetEventsAsync's own endpoint.</summary>
    public async Task<int> GetTotalCountAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.audit_event");
    }

    /// <summary>D-124 Phase 3: how many rows match the current filter (ignoring paging) -- backs the new X-Filtered-Count header, distinct from GetTotalCountAsync's unfiltered grand total.</summary>
    public async Task<int> GetFilteredCountAsync(string? eventTypeName, string? entityName, int? performedByUserKey, DateTime? fromDate, DateTime? toDate)
    {
        using var connection = connectionFactory.Create();
        var sql = $"SELECT COUNT(*) {FilterFromSql}";
        return await connection.QuerySingleAsync<int>(sql, new
        {
            EventTypeName = eventTypeName,
            EntityName = entityName,
            PerformedByUserKey = performedByUserKey,
            FromDate = fromDate,
            ToDate = toDate
        });
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
