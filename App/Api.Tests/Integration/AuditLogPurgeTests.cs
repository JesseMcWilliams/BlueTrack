using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// Design_Audit-Logging.md D-62: usp_PurgeAuditLog (Database/39), designed
/// 2026-08-27 alongside web.audit_purge_log but not actually written until
/// 2026-09-21. web.audit_config is a singleton (exactly one row) shared by
/// every test in this suite, so every test here saves and restores
/// RetentionDays around its own run rather than assuming a starting value.
/// </summary>
public class AuditLogPurgeTests
{
    [Fact]
    public async Task PurgeAuditLog_DeletesEventsOlderThanRetentionDays_KeepsNewerOnes_AndCascadesFieldChanges()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var originalRetentionDays = await GetRetentionDaysAsync(connection);
        var userKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var entityName = $"IntegrationTestPurge_{Guid.NewGuid():N}";

        var oldEventKey = await InsertAuditEventAsync(connection, userKey, entityName, "old", occurredAt: DateTime.UtcNow.AddDays(-40));
        await InsertFieldChangeAsync(connection, oldEventKey, "SomeField", "before", "after");
        var newEventKey = await InsertAuditEventAsync(connection, userKey, entityName, "new", occurredAt: DateTime.UtcNow.AddDays(-1));

        try
        {
            await SetRetentionDaysAsync(connection, 30);

            await connection.ExecuteAsync("EXEC dbo.usp_PurgeAuditLog");

            var oldStillExists = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM web.audit_event WHERE AuditEventKey = @Key", new { Key = oldEventKey });
            var oldFieldChangeStillExists = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM web.audit_field_change WHERE AuditEventKey = @Key", new { Key = oldEventKey });
            var newStillExists = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM web.audit_event WHERE AuditEventKey = @Key", new { Key = newEventKey });

            Assert.Equal(0, oldStillExists);
            Assert.Equal(0, oldFieldChangeStillExists);
            Assert.Equal(1, newStillExists);

            var lastPurge = await connection.QuerySingleAsync<(string Status, int? RowsPurged)>(
                "SELECT TOP 1 Status, RowsPurged FROM web.audit_purge_log ORDER BY StartedAt DESC");
            Assert.Equal("Succeeded", lastPurge.Status);
            Assert.True(lastPurge.RowsPurged >= 1);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.audit_field_change WHERE AuditEventKey IN (@Old, @New)", new { Old = oldEventKey, New = newEventKey });
            await connection.ExecuteAsync("DELETE FROM web.audit_event WHERE AuditEventKey IN (@Old, @New)", new { Old = oldEventKey, New = newEventKey });
            await RestoreRetentionDaysAsync(connection, originalRetentionDays);
        }
    }

    [Fact]
    public async Task PurgeAuditLog_RetentionDaysNull_SkipsWithoutDeletingAnything()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var originalRetentionDays = await GetRetentionDaysAsync(connection);
        var userKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var entityName = $"IntegrationTestPurgeSkip_{Guid.NewGuid():N}";

        var veryOldEventKey = await InsertAuditEventAsync(connection, userKey, entityName, "veryOld", occurredAt: DateTime.UtcNow.AddYears(-5));

        try
        {
            await SetRetentionDaysAsync(connection, null);

            await connection.ExecuteAsync("EXEC dbo.usp_PurgeAuditLog");

            var stillExists = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM web.audit_event WHERE AuditEventKey = @Key", new { Key = veryOldEventKey });
            Assert.Equal(1, stillExists);

            var lastPurge = await connection.QuerySingleAsync<(string Status, string? ErrorMessage)>(
                "SELECT TOP 1 Status, ErrorMessage FROM web.audit_purge_log ORDER BY StartedAt DESC");
            Assert.Equal("Skipped", lastPurge.Status);
            Assert.NotNull(lastPurge.ErrorMessage);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.audit_event WHERE AuditEventKey = @Key", new { Key = veryOldEventKey });
            await RestoreRetentionDaysAsync(connection, originalRetentionDays);
        }
    }

    private static async Task<int?> GetRetentionDaysAsync(SqlConnection connection) =>
        await connection.QuerySingleAsync<int?>("SELECT RetentionDays FROM web.audit_config");

    private static async Task SetRetentionDaysAsync(SqlConnection connection, int? retentionDays) =>
        await connection.ExecuteAsync("UPDATE web.audit_config SET RetentionDays = @RetentionDays", new { RetentionDays = retentionDays });

    private static async Task RestoreRetentionDaysAsync(SqlConnection connection, int? originalRetentionDays) =>
        await connection.ExecuteAsync("UPDATE web.audit_config SET RetentionDays = @RetentionDays", new { RetentionDays = originalRetentionDays });

    private static async Task<long> InsertAuditEventAsync(SqlConnection connection, int performedByUserKey, string entityName, string entityKey, DateTime occurredAt) =>
        await connection.QuerySingleAsync<long>("""
            INSERT INTO web.audit_event (AuditEventTypeKey, PerformedByUserKey, EntityName, EntityKey, OccurredAt)
            OUTPUT inserted.AuditEventKey
            SELECT (SELECT AuditEventTypeKey FROM web.dim_audit_event_type WHERE EventTypeName = 'FieldEdit'),
                   @PerformedByUserKey, @EntityName, @EntityKey, @OccurredAt
            """, new { PerformedByUserKey = performedByUserKey, EntityName = entityName, EntityKey = entityKey, OccurredAt = occurredAt });

    private static async Task InsertFieldChangeAsync(SqlConnection connection, long auditEventKey, string fieldName, string oldValue, string newValue) =>
        await connection.ExecuteAsync(
            "INSERT INTO web.audit_field_change (AuditEventKey, FieldName, OldValue, NewValue) VALUES (@AuditEventKey, @FieldName, @OldValue, @NewValue)",
            new { AuditEventKey = auditEventKey, FieldName = fieldName, OldValue = oldValue, NewValue = newValue });
}
