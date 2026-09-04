using BlueTrack.Api.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// web.audit_event/web.audit_field_change back the Audit Log Viewer
/// (Design_Audit_Logging.md). web.audit_event accumulates real rows from
/// every other test run (AuditLogger.LogAsync is called by many
/// controllers), so every test here inserts its own event with a unique
/// EntityName/EntityKey and filters on that rather than assuming the log
/// is otherwise empty -- the same lesson already applied to
/// permission-boundaries.spec.js's E2E assertions.
/// </summary>
public class AuditRepositoryTests
{
    private static AuditRepository CreateRepository() => new(new TestDbConnectionFactory());

    [Fact]
    public async Task GetEventsAsync_FiltersByEntityName()
    {
        var repository = CreateRepository();
        var entityName = $"IntegrationTestEntity_{Guid.NewGuid():N}";
        var userKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        await InsertAuditEventAsync("FieldEdit", userKey, entityName, entityKey: "1", detail: "Integration test event");

        var results = await repository.GetEventsAsync(eventTypeName: null, entityName: entityName, performedByUserKey: null, fromDate: null, toDate: null);

        var match = Assert.Single(results);
        Assert.Equal(entityName, match.EntityName);
        Assert.Equal("FieldEdit", match.EventTypeName);
        Assert.Equal("Integration test event", match.Detail);
    }

    [Fact]
    public async Task GetEventsAsync_FiltersByPerformedByUserKey()
    {
        var repository = CreateRepository();
        var entityName = $"IntegrationTestEntity_{Guid.NewGuid():N}";
        var user1Key = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var user2Key = await TestUsers.GetUserKeyAsync("IntegrationTestUser2");
        await InsertAuditEventAsync("FieldEdit", user1Key, entityName, entityKey: "1");
        await InsertAuditEventAsync("FieldEdit", user2Key, entityName, entityKey: "2");

        var results = await repository.GetEventsAsync(eventTypeName: null, entityName: entityName, performedByUserKey: user1Key, fromDate: null, toDate: null);

        var match = Assert.Single(results);
        Assert.Equal("1", match.EntityKey);
    }

    [Fact]
    public async Task GetEventsAsync_SortByOccurredAtAscending_OrdersOldestFirst()
    {
        var repository = CreateRepository();
        var entityName = $"IntegrationTestEntity_{Guid.NewGuid():N}";
        var userKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var firstKey = await InsertAuditEventAsync("FieldEdit", userKey, entityName, entityKey: "first");
        var secondKey = await InsertAuditEventAsync("FieldEdit", userKey, entityName, entityKey: "second");

        var results = await repository.GetEventsAsync(
            eventTypeName: null, entityName: entityName, performedByUserKey: null, fromDate: null, toDate: null,
            sortBy: [("occurredAt", false)]);

        Assert.Equal(2, results.Count);
        Assert.Equal(firstKey, results[0].AuditEventKey);
        Assert.Equal(secondKey, results[1].AuditEventKey);
    }

    [Fact]
    public async Task GetEventsAsync_SqlInjectionAttemptAsSortField_IsIgnoredNotExecuted()
    {
        var repository = CreateRepository();

        var results = await repository.GetEventsAsync(
            eventTypeName: null, entityName: null, performedByUserKey: null, fromDate: null, toDate: null,
            sortBy: [("occurredAt; DROP TABLE web.audit_event; --", false)]);

        Assert.NotNull(results);
    }

    [Fact]
    public async Task GetFieldChangesAsync_ReturnsTheChangesForThatEventOnly()
    {
        var repository = CreateRepository();
        var entityName = $"IntegrationTestEntity_{Guid.NewGuid():N}";
        var userKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var eventKey = await InsertAuditEventAsync("FieldEdit", userKey, entityName, entityKey: "1");
        await InsertFieldChangeAsync(eventKey, "StatusName", "Not Started", "In Progress");
        await InsertFieldChangeAsync(eventKey, "OwnerName", null, "Integration Test Owner");
        var otherEventKey = await InsertAuditEventAsync("FieldEdit", userKey, entityName, entityKey: "2");
        await InsertFieldChangeAsync(otherEventKey, "StatusName", "In Progress", "Complete");

        var changes = await repository.GetFieldChangesAsync(eventKey);

        Assert.Equal(2, changes.Count);
        Assert.Contains(changes, c => c.FieldName == "StatusName" && c.OldValue == "Not Started" && c.NewValue == "In Progress");
        Assert.Contains(changes, c => c.FieldName == "OwnerName" && c.OldValue == null && c.NewValue == "Integration Test Owner");
    }

    private static async Task<long> InsertAuditEventAsync(string eventTypeName, int performedByUserKey, string entityName, string? entityKey = null, string? detail = null)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<long>("""
            INSERT INTO web.audit_event (AuditEventTypeKey, PerformedByUserKey, EntityName, EntityKey, Detail)
            OUTPUT inserted.AuditEventKey
            SELECT (SELECT AuditEventTypeKey FROM web.dim_audit_event_type WHERE EventTypeName = @EventTypeName),
                   @PerformedByUserKey, @EntityName, @EntityKey, @Detail
            """, new { EventTypeName = eventTypeName, PerformedByUserKey = performedByUserKey, EntityName = entityName, EntityKey = entityKey, Detail = detail });
    }

    private static async Task InsertFieldChangeAsync(long auditEventKey, string fieldName, string? oldValue, string? newValue)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync(
            "INSERT INTO web.audit_field_change (AuditEventKey, FieldName, OldValue, NewValue) VALUES (@AuditEventKey, @FieldName, @OldValue, @NewValue)",
            new { AuditEventKey = auditEventKey, FieldName = fieldName, OldValue = oldValue, NewValue = newValue });
    }
}
