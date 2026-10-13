using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-210: reads back the ConnectionTest audit events the Test buttons write.</summary>
internal static class ConnectionTestAudit
{
    /// <summary>The newest ConnectionTest event's Detail for this entity whose Detail or EntityKey contains <paramref name="marker"/>; null if none.</summary>
    public static async Task<string?> LatestDetailAsync(string entityName, string marker)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QueryFirstOrDefaultAsync<string>("""
            SELECT TOP (1) e.Detail
            FROM web.audit_event e
            JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey
            WHERE t.EventTypeName = 'ConnectionTest' AND e.EntityName = @EntityName
              AND (e.EntityKey = @Marker OR e.Detail LIKE '%' + @Marker + '%')
            ORDER BY e.AuditEventKey DESC
            """, new { EntityName = entityName, Marker = marker });
    }
}
