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

            var purge = await RunPurgeAsync(connection);

            var oldStillExists = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM web.audit_event WHERE AuditEventKey = @Key", new { Key = oldEventKey });
            var oldFieldChangeStillExists = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM web.audit_field_change WHERE AuditEventKey = @Key", new { Key = oldEventKey });
            var newStillExists = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM web.audit_event WHERE AuditEventKey = @Key", new { Key = newEventKey });

            Assert.Equal(0, oldStillExists);
            Assert.Equal(0, oldFieldChangeStillExists);
            Assert.Equal(1, newStillExists);

            Assert.Equal("Succeeded", purge.Status);
            Assert.True(purge.RowsPurged >= 1);
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

            var purge = await RunPurgeAsync(connection);

            var stillExists = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM web.audit_event WHERE AuditEventKey = @Key", new { Key = veryOldEventKey });
            Assert.Equal(1, stillExists);

            Assert.Equal("Skipped", purge.Status);
            Assert.NotNull(purge.ErrorMessage);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.audit_event WHERE AuditEventKey = @Key", new { Key = veryOldEventKey });
            await RestoreRetentionDaysAsync(connection, originalRetentionDays);
        }
    }

    // Runs the purge and returns the log row it wrote -- found as the one row
    // that wasn't there before, not as the newest StartedAt: two purges run
    // back to back can share a StartedAt tick, so "newest" could be the
    // previous test's row.
    private static async Task<(string Status, int? RowsPurged, string? ErrorMessage)> RunPurgeAsync(SqlConnection connection)
    {
        var earlier = (await connection.QueryAsync<Guid>("SELECT PurgeBatchId FROM web.audit_purge_log")).ToHashSet();
        await connection.ExecuteAsync("EXEC dbo.usp_PurgeAuditLog");
        var rows = await connection.QueryAsync<(Guid PurgeBatchId, string Status, int? RowsPurged, string? ErrorMessage)>(
            "SELECT PurgeBatchId, Status, RowsPurged, ErrorMessage FROM web.audit_purge_log");
        var mine = Assert.Single(rows, r => !earlier.Contains(r.PurgeBatchId));
        return (mine.Status, mine.RowsPurged, mine.ErrorMessage);
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
