using Dapper;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Audit;

/// <summary>
/// Writes to web.audit_event/web.audit_field_change (Design_Audit_Logging.md,
/// D-10/D-11). Logon auditing is handled separately by UserRightsResolver's
/// cache-miss detection (D-82), not through this class directly.
/// </summary>
public sealed class AuditLogger(IDbConnectionFactory connectionFactory, IHttpContextAccessor httpContextAccessor, AppConfigRepository appConfigRepository)
{
    public async Task LogAsync(
        string eventTypeName,
        int performedByUserKey,
        string? entityName = null,
        string? entityKey = null,
        string? detail = null,
        string? reason = null,
        IReadOnlyList<FieldChange>? fieldChanges = null)
    {
        using var connection = connectionFactory.Create();

        var sourceIpAddress = httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

        const string insertEventSql = """
            INSERT INTO web.audit_event
                (AuditEventTypeKey, PerformedByUserKey, EntityName, EntityKey, SourceIpAddress, Detail, Reason)
            OUTPUT inserted.AuditEventKey
            SELECT (SELECT AuditEventTypeKey FROM web.dim_audit_event_type WHERE EventTypeName = @EventTypeName),
                   @PerformedByUserKey, @EntityName, @EntityKey, @SourceIpAddress, @Detail, @Reason
            """;

        var auditEventKey = await connection.QuerySingleAsync<long>(insertEventSql, new
        {
            EventTypeName = eventTypeName,
            PerformedByUserKey = performedByUserKey,
            EntityName = entityName,
            EntityKey = entityKey,
            SourceIpAddress = sourceIpAddress,
            Detail = detail,
            Reason = reason
        });

        if (fieldChanges is not { Count: > 0 })
        {
            return;
        }

        const string insertFieldChangeSql = """
            INSERT INTO web.audit_field_change (AuditEventKey, FieldName, OldValue, NewValue)
            VALUES (@AuditEventKey, @FieldName, @OldValue, @NewValue)
            """;

        foreach (var change in fieldChanges)
        {
            await connection.ExecuteAsync(insertFieldChangeSql, new
            {
                AuditEventKey = auditEventKey,
                change.FieldName,
                change.OldValue,
                change.NewValue
            });
        }
    }

    /// <summary>
    /// D-35/D-83: logs a RecordViewed event for a detail-view GET, but only
    /// when audit_config.LogReadEvents is on -- scoped to GET-by-key detail
    /// endpoints only (Account Progress detail, Risk Exception detail), not
    /// list/search/report endpoints, per the user's explicit choice
    /// 2026-09-04 to avoid flooding the log on every list page load.
    /// </summary>
    public async Task LogReadIfEnabledAsync(int performedByUserKey, string entityName, string entityKey)
    {
        if (!await appConfigRepository.IsLogReadEventsEnabledAsync())
        {
            return;
        }

        await LogAsync("RecordViewed", performedByUserKey, entityName, entityKey);
    }
}
