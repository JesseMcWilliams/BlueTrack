using Dapper;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Audit;

/// <summary>
/// Writes to web.audit_event/web.audit_field_change (Design_Audit_Logging.md,
/// D-10/D-11). Scoped to writes/approvals only, per D-35 -- logons and reads
/// aren't wired here yet: Logon would fire on every /api/me call rather
/// than once per real session (there's no session layer to distinguish
/// them, the same gap PermissionClaimsTransformation's own comment
/// documents), and LogReadEvents has no enforcement point yet even though
/// the config field exists (GlobalApplicationConfiguration admin page).
/// Both are follow-ups once this app has a real session concept.
/// </summary>
public sealed class AuditLogger(IDbConnectionFactory connectionFactory, IHttpContextAccessor httpContextAccessor)
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
}
