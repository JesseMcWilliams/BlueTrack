using BlueTrack.Api.Audit;
using BlueTrack.Api.Secrets;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-295: the audit log's seals. Other tests delete their own audit rows directly, so the test database's chain as a
/// whole isn't intact; each test here verifies from its own first seal and looks only at problems with its own events.
/// </summary>
public class AuditChainTests
{
    private static AuditChainService Service() => new(new TestDbConnectionFactory(), new WindowsDpapiProtector());

    private sealed record TestEvent(long Key, DateTime OccurredAt, string EntityName, string Detail);

    private static async Task<TestEvent> InsertEventAsync(SqlConnection connection, string tag, DateTime? occurredAt = null, bool withFieldChange = true)
    {
        var userKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var when = occurredAt ?? DateTime.UtcNow;
        var entityName = $"AuditChainTest_{tag}";
        var detail = $"Chain test {tag}";
        // The event and its field change in one transaction, as the API writes them (so it is never sealed without it).
        await using var transaction = connection.BeginTransaction();
        var key = await connection.QuerySingleAsync<long>("""
            INSERT INTO web.audit_event (AuditEventTypeKey, PerformedByUserKey, EntityName, EntityKey, Detail, OccurredAt)
            OUTPUT inserted.AuditEventKey
            SELECT (SELECT AuditEventTypeKey FROM web.dim_audit_event_type WHERE EventTypeName = 'FieldEdit'), @userKey, @entityName, 'k', @detail, @when
            """, new { userKey, entityName, detail, when }, transaction);
        if (withFieldChange)
        {
            await connection.ExecuteAsync(
                "INSERT INTO web.audit_field_change (AuditEventKey, FieldName, OldValue, NewValue) VALUES (@key, 'Notes', NULL, 'chain')",
                new { key }, transaction);
        }
        await transaction.CommitAsync();
        return new TestEvent(key, when, entityName, detail);
    }

    private static async Task<long> SealOfAsync(SqlConnection connection, long eventKey) =>
        await connection.QuerySingleAsync<long>("SELECT ChainSeq FROM web.audit_chain WHERE AuditEventKey = @eventKey", new { eventKey });

    private static async Task DeleteAsync(SqlConnection connection, params TestEvent[] events)
    {
        var keys = events.Select(e => e.Key).ToArray();
        await connection.ExecuteAsync("""
            DELETE FROM web.audit_field_change WHERE AuditEventKey IN @keys;
            DELETE FROM web.audit_event WHERE AuditEventKey IN @keys;
            """, new { keys });
    }

    private static IEnumerable<AuditChainProblem> ProblemsWith(AuditChainVerifyResult result, params TestEvent[] events) =>
        result.Problems.Where(p => p.AuditEventKey is { } key && events.Any(e => e.Key == key));

    [Fact]
    public async Task SealedEvents_Verify_AndAnEditToAnEventIsFound_AndGoesAwayWhenUndone()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var tag = Guid.NewGuid().ToString("N")[..10];
        var a = await InsertEventAsync(connection, tag + "a");
        var b = await InsertEventAsync(connection, tag + "b");
        try
        {
            var service = Service();
            await service.SealAllAsync();
            var from = await SealOfAsync(connection, a.Key);
            Assert.True(await SealOfAsync(connection, b.Key) > from);

            Assert.Empty(ProblemsWith(await service.VerifyAsync(fromChainSeq: from), a, b));

            await connection.ExecuteAsync("UPDATE web.audit_event SET Detail = 'tampered' WHERE AuditEventKey = @key", new { key = a.Key });
            var broken = await service.VerifyAsync(fromChainSeq: from);
            var problem = Assert.Single(ProblemsWith(broken, a, b));
            Assert.Equal(("Edited", a.Key), (problem.Kind, problem.AuditEventKey!.Value));
            Assert.False(broken.Intact);

            // A field change counts too.
            await connection.ExecuteAsync("UPDATE web.audit_event SET Detail = @detail WHERE AuditEventKey = @key", new { key = a.Key, detail = a.Detail });
            await connection.ExecuteAsync("UPDATE web.audit_field_change SET NewValue = 'other' WHERE AuditEventKey = @key", new { key = b.Key });
            Assert.Equal([("Edited", b.Key)], ProblemsWith(await service.VerifyAsync(fromChainSeq: from), a, b).Select(p => (p.Kind, p.AuditEventKey!.Value)));

            await connection.ExecuteAsync("UPDATE web.audit_field_change SET NewValue = 'chain' WHERE AuditEventKey = @key", new { key = b.Key });
            Assert.Empty(ProblemsWith(await service.VerifyAsync(fromChainSeq: from), a, b));
        }
        finally
        {
            await DeleteAsync(connection, a, b);
        }
    }

    [Fact]
    public async Task ADeletedEvent_IsFound()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var tag = Guid.NewGuid().ToString("N")[..10];
        var a = await InsertEventAsync(connection, tag + "a");
        var b = await InsertEventAsync(connection, tag + "b");
        try
        {
            var service = Service();
            await service.SealAllAsync();
            var from = await SealOfAsync(connection, a.Key);

            await DeleteAsync(connection, a);

            var problem = Assert.Single(ProblemsWith(await service.VerifyAsync(fromChainSeq: from), a, b));
            Assert.Equal(("EventDeleted", a.Key), (problem.Kind, problem.AuditEventKey!.Value));
        }
        finally
        {
            await DeleteAsync(connection, a, b);
        }
    }

    [Fact]
    public async Task TheRetentionPurge_RecordsWhatItDeleted_SoTheChainStillVerifiesAcrossIt()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var tag = Guid.NewGuid().ToString("N")[..10];
        var old = await InsertEventAsync(connection, tag + "old", occurredAt: DateTime.UtcNow.AddDays(-400));
        var after = await InsertEventAsync(connection, tag + "new");
        var retention = await connection.QuerySingleAsync<int?>("SELECT RetentionDays FROM web.audit_config");
        try
        {
            var service = Service();
            await service.SealAllAsync();
            var oldSeal = await SealOfAsync(connection, old.Key);
            var afterSeal = await SealOfAsync(connection, after.Key);

            await connection.ExecuteAsync("UPDATE web.audit_config SET RetentionDays = 365");
            await connection.ExecuteAsync("EXEC dbo.usp_PurgeAuditLog");

            Assert.Equal(0, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.audit_event WHERE AuditEventKey = @key", new { key = old.Key }));
            Assert.Equal(0, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.audit_chain WHERE ChainSeq = @oldSeal", new { oldSeal }));
            Assert.Equal(1, await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM web.audit_chain_purged WHERE FromChainSeq <= @oldSeal AND ToChainSeq >= @oldSeal", new { oldSeal }));

            // From the purged seal on: the gap is bridged, and the event after it still checks.
            var result = await service.VerifyAsync(fromChainSeq: oldSeal);
            Assert.Empty(ProblemsWith(result, after));
            Assert.DoesNotContain(result.Problems, p => p.Kind == "SealMissing" && p.ChainSeq == oldSeal);
            Assert.True(afterSeal > oldSeal);
        }
        finally
        {
            await connection.ExecuteAsync("UPDATE web.audit_config SET RetentionDays = @retention", new { retention });
            await DeleteAsync(connection, old, after);
        }
    }

    [Fact]
    public async Task AnEventNotSealedYet_IsKeptByThePurge()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var tag = Guid.NewGuid().ToString("N")[..10];
        var retention = await connection.QuerySingleAsync<int?>("SELECT RetentionDays FROM web.audit_config");
        TestEvent? old = null;
        try
        {
            await connection.ExecuteAsync("UPDATE web.audit_config SET RetentionDays = 365");
            // Insert and purge in one transaction, so no sealer (another test's API host) seals it in between.
            await using var transaction = connection.BeginTransaction();
            var userKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
            var key = await connection.QuerySingleAsync<long>("""
                INSERT INTO web.audit_event (AuditEventTypeKey, PerformedByUserKey, EntityName, EntityKey, Detail, OccurredAt)
                OUTPUT inserted.AuditEventKey
                SELECT (SELECT AuditEventTypeKey FROM web.dim_audit_event_type WHERE EventTypeName = 'FieldEdit'), @userKey, @name, 'k', 'unsealed', DATEADD(DAY, -400, SYSUTCDATETIME())
                """, new { userKey, name = $"AuditChainTest_{tag}" }, transaction);
            old = new TestEvent(key, DateTime.UtcNow, "", "");
            await connection.ExecuteAsync("EXEC dbo.usp_PurgeAuditLog", transaction: transaction);
            Assert.Equal(1, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.audit_event WHERE AuditEventKey = @key", new { key }, transaction));
            await transaction.CommitAsync();
        }
        finally
        {
            await connection.ExecuteAsync("UPDATE web.audit_config SET RetentionDays = @retention", new { retention });
            if (old is not null) await DeleteAsync(connection, old);
        }
    }

    [Fact]
    public void TheCanonicalText_TellsNullEmptyAndDashApart_AndTheSealDependsOnThePreviousOne()
    {
        AuditChainEvent Event(string? detail) => new() { AuditEventKey = 1, AuditEventTypeKey = 2, OccurredAt = new DateTime(2026, 10, 21, 1, 2, 3), PerformedByUserKey = 3, Detail = detail };
        var texts = new[] { Event(null), Event(""), Event("-") }.Select(AuditChainHasher.Canonical).ToList();
        Assert.Equal(3, texts.Distinct().Count());

        var key = new byte[32];
        var first = AuditChainHasher.Compute(key, null, Event("x"));
        Assert.Equal(32, first.Length);
        Assert.NotEqual(first, AuditChainHasher.Compute(key, first, Event("x")));
        Assert.NotEqual(first, AuditChainHasher.Compute(new byte[32].Select(_ => (byte)1).ToArray(), null, Event("x")));
    }
}
