using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BlueTrack.Api.Data;
using BlueTrack.Api.Secrets;
using Dapper;

namespace BlueTrack.Api.Audit;

/// <summary>D-295: one audit event as it is sealed: its own columns and its field changes, in order.</summary>
public sealed class AuditChainEvent
{
    public long AuditEventKey { get; init; }
    public int AuditEventTypeKey { get; init; }
    public DateTime OccurredAt { get; init; }
    public int PerformedByUserKey { get; init; }
    public string? EntityName { get; init; }
    public string? EntityKey { get; init; }
    public string? SourceIpAddress { get; init; }
    public string? Detail { get; init; }
    public string? Reason { get; init; }
    public List<(string FieldName, string? OldValue, string? NewValue)> FieldChanges { get; } = [];
}

/// <summary>
/// D-295: the seal. HMAC-SHA256, keyed, over the previous seal (32 zero bytes for the first)
/// followed by the event's canonical text: every value length-prefixed ("5:Hello", "-" for null) and joined by "|",
/// so no two different events can produce the same text.
/// </summary>
public static class AuditChainHasher
{
    public const int HashLength = 32;
    private static readonly byte[] NoPrevious = new byte[HashLength];

    public static string Canonical(AuditChainEvent e)
    {
        var parts = new List<string?>
        {
            "BlueTrack-audit-v1",
            e.AuditEventKey.ToString(CultureInfo.InvariantCulture),
            e.AuditEventTypeKey.ToString(CultureInfo.InvariantCulture),
            e.OccurredAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff", CultureInfo.InvariantCulture),
            e.PerformedByUserKey.ToString(CultureInfo.InvariantCulture),
            e.EntityName, e.EntityKey, e.SourceIpAddress, e.Detail, e.Reason,
            e.FieldChanges.Count.ToString(CultureInfo.InvariantCulture),
        };
        foreach (var (field, oldValue, newValue) in e.FieldChanges)
        {
            parts.Add(field);
            parts.Add(oldValue);
            parts.Add(newValue);
        }
        return string.Join("|", parts.Select(p => p is null ? "-" : string.Create(CultureInfo.InvariantCulture, $"{p.Length}:{p}")));
    }

    public static byte[] Compute(byte[] key, byte[]? previous, AuditChainEvent e)
    {
        var text = Encoding.UTF8.GetBytes(Canonical(e));
        var input = new byte[HashLength + text.Length];
        (previous ?? NoPrevious).CopyTo(input, 0);
        text.CopyTo(input, HashLength);
        return HMACSHA256.HashData(key, input);
    }
}

public sealed record AuditChainProblem(long? ChainSeq, long? AuditEventKey, string Kind);

/// <summary>D-295: what Verify found. Kinds: Edited, EventDeleted, SealMissing, SealsMissingAtEnd.</summary>
public sealed record AuditChainVerifyResult(
    bool Intact, int Checked, long? FirstChainSeq, long? LastChainSeq, int Unsealed, DateTime? LastSealedAt,
    IReadOnlyList<AuditChainProblem> Problems, bool MoreProblems)
{
    public string Summary => Intact
        ? string.Create(CultureInfo.InvariantCulture, $"Intact: {Checked} sealed events checked, {Unsealed} waiting to be sealed.")
        : string.Create(CultureInfo.InvariantCulture,
            $"Broken: {Problems.Count}{(MoreProblems ? "+" : "")} problem(s) in {Checked} sealed events; first: {Problems[0].Kind} at seal {Problems[0].ChainSeq?.ToString(CultureInfo.InvariantCulture) ?? "?"} (audit event {Problems[0].AuditEventKey?.ToString(CultureInfo.InvariantCulture) ?? "?"}).");
}

public sealed record AuditChainStatus(long LastChainSeq, DateTime? LastSealedAt, int Unsealed,
    DateTime? LastVerifiedAt, bool? LastVerifyIntact, string? LastVerifySummary);

/// <summary>
/// D-295: seals and verifies the audit log. Sealing runs one batch at a time under an update lock on the single
/// web.audit_chain_state row, so two API processes never seal at once; it reads, like the SIEM forwarder, only
/// committed rows (WriteVersion below MIN_ACTIVE_ROWVERSION()) in WriteVersion order, so an event is never sealed
/// before its field changes exist and one committed late is never skipped. The HMAC key is made on first use and
/// stored protected (ILocalSecretProtector.ProtectStored: DPAPI under the app pool account), so reading the
/// database alone doesn't give it.
/// </summary>
public sealed class AuditChainService(IDbConnectionFactory connectionFactory, ILocalSecretProtector protector)
{
    public const int SealBatchSize = 500;
    private const int VerifyBatchSize = 2000;
    public const int MaxProblemsListed = 50;

    private readonly Dictionary<int, byte[]> _keys = [];
    private readonly SemaphoreSlim _keyLock = new(1, 1);

    private async Task<(int KeyId, byte[] Key)> CurrentKeyAsync()
    {
        await _keyLock.WaitAsync();
        try
        {
            using var connection = connectionFactory.Create();
            var row = await connection.QueryFirstOrDefaultAsync<(int Id, string Protected)>(
                "SELECT TOP (1) AuditChainKeyId, ProtectedKey FROM web.audit_chain_key ORDER BY AuditChainKeyId DESC");
            if (row.Protected is null)
            {
                var fresh = RandomNumberGenerator.GetBytes(32);
                var id = await connection.QuerySingleAsync<int>(
                    "INSERT INTO web.audit_chain_key (ProtectedKey) OUTPUT inserted.AuditChainKeyId VALUES (@p)",
                    new { p = protector.ProtectStored(Convert.ToBase64String(fresh)) });
                _keys[id] = fresh;
                return (id, fresh);
            }
            if (!_keys.TryGetValue(row.Id, out var key))
            {
                key = Convert.FromBase64String(protector.UnprotectStored(row.Protected));
                _keys[row.Id] = key;
            }
            return (row.Id, key);
        }
        finally
        {
            _keyLock.Release();
        }
    }

    private async Task<byte[]> KeyAsync(int keyId)
    {
        await _keyLock.WaitAsync();
        try
        {
            if (_keys.TryGetValue(keyId, out var key)) return key;
            using var connection = connectionFactory.Create();
            var protectedKey = await connection.QuerySingleOrDefaultAsync<string?>(
                "SELECT ProtectedKey FROM web.audit_chain_key WHERE AuditChainKeyId = @keyId", new { keyId })
                ?? throw new InvalidOperationException($"Audit chain key {keyId} is missing.");
            key = Convert.FromBase64String(protector.UnprotectStored(protectedKey));
            _keys[keyId] = key;
            return key;
        }
        finally
        {
            _keyLock.Release();
        }
    }

    private static async Task<List<AuditChainEvent>> ReadEventsAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction? transaction, string sql, object parameters)
    {
        var events = (await connection.QueryAsync<AuditChainEvent>(sql, parameters, transaction)).AsList();
        if (events.Count == 0) return events;
        var keys = events.Select(e => e.AuditEventKey).ToList();
        var byKey = events.ToDictionary(e => e.AuditEventKey);
        var changes = await connection.QueryAsync<(long AuditEventKey, string FieldName, string? OldValue, string? NewValue)>("""
            SET LOCK_TIMEOUT 5000;
            SELECT AuditEventKey, FieldName, OldValue, NewValue FROM web.audit_field_change
            WHERE AuditEventKey IN @keys ORDER BY AuditFieldChangeKey
            """, new { keys }, transaction);
        foreach (var c in changes) byKey[c.AuditEventKey].FieldChanges.Add((c.FieldName, c.OldValue, c.NewValue));
        return events;
    }

    /// <summary>Seals the next batch of committed, unsealed events. Returns how many it sealed.</summary>
    public async Task<int> SealOnceAsync()
    {
        var (keyId, key) = await CurrentKeyAsync();
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var state = await connection.QuerySingleAsync<(byte[] LookedAtVersion, long LastChainSeq, byte[]? LastChainHash)>("""
            SET LOCK_TIMEOUT 5000;
            SELECT LookedAtVersion, LastChainSeq, LastChainHash FROM web.audit_chain_state WITH (UPDLOCK, HOLDLOCK) WHERE AuditChainStateKey = 1
            """, transaction: transaction);

        var events = await ReadEventsAsync(connection, transaction, """
            SET LOCK_TIMEOUT 5000;
            DECLARE @Below BINARY(8) = MIN_ACTIVE_ROWVERSION();
            SELECT TOP (@batch) ae.AuditEventKey, ae.AuditEventTypeKey, ae.OccurredAt, ae.PerformedByUserKey,
                   ae.EntityName, ae.EntityKey, ae.SourceIpAddress, ae.Detail, ae.Reason, ae.WriteVersion
            FROM web.audit_event ae WITH (INDEX(IX_audit_event_WriteVersion))
            WHERE ae.WriteVersion > @lookedAt AND ae.WriteVersion < @Below
              AND NOT EXISTS (SELECT 1 FROM web.audit_chain c WHERE c.AuditEventKey = ae.AuditEventKey)
            ORDER BY ae.WriteVersion
            """, new { batch = SealBatchSize, lookedAt = state.LookedAtVersion });
        if (events.Count == 0)
        {
            transaction.Commit();
            return 0;
        }

        var lastVersion = await connection.QuerySingleAsync<byte[]>(
            "SELECT WriteVersion FROM web.audit_event WHERE AuditEventKey = @key", new { key = events[^1].AuditEventKey }, transaction);
        var previous = state.LastChainHash;
        var seq = state.LastChainSeq;
        var seals = new List<object>(events.Count);
        foreach (var e in events)
        {
            previous = AuditChainHasher.Compute(key, previous, e);
            seq++;
            seals.Add(new { ChainSeq = seq, e.AuditEventKey, KeyId = keyId, ChainHash = previous });
        }
        await connection.ExecuteAsync(
            "INSERT INTO web.audit_chain (ChainSeq, AuditEventKey, AuditChainKeyId, ChainHash) VALUES (@ChainSeq, @AuditEventKey, @KeyId, @ChainHash)",
            seals, transaction);
        await connection.ExecuteAsync("""
            UPDATE web.audit_chain_state
            SET LookedAtVersion = @lastVersion, LastChainSeq = @seq, LastChainHash = @previous, LastSealedAt = SYSUTCDATETIME()
            WHERE AuditChainStateKey = 1
            """, new { lastVersion, seq, previous }, transaction);
        transaction.Commit();
        return events.Count;
    }

    /// <summary>Seals until nothing is left (tests, and Verify, so it checks everything committed so far).</summary>
    public async Task SealAllAsync()
    {
        while (await SealOnceAsync() == SealBatchSize) { }
    }

    public async Task<AuditChainStatus> GetStatusAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<AuditChainStatus>("""
            SELECT s.LastChainSeq, s.LastSealedAt,
                   (SELECT COUNT(*) FROM web.audit_event ae WITH (INDEX(IX_audit_event_WriteVersion))
                    WHERE ae.WriteVersion > s.LookedAtVersion
                      AND NOT EXISTS (SELECT 1 FROM web.audit_chain c WHERE c.AuditEventKey = ae.AuditEventKey)) AS Unsealed,
                   s.LastVerifiedAt, s.LastVerifyIntact, s.LastVerifySummary
            FROM web.audit_chain_state s WHERE s.AuditChainStateKey = 1
            """);
    }

    private sealed class SealRow
    {
        public long ChainSeq { get; init; }
        public long AuditEventKey { get; init; }
        public int AuditChainKeyId { get; init; }
        public byte[] ChainHash { get; init; } = [];
    }

    /// <summary>
    /// Seals what is waiting, then recomputes every seal: each against the stored seal before it, so one edited event
    /// shows as one problem. A gap in ChainSeq is fine where the retention purge recorded deleting exactly that run
    /// (web.audit_chain_purged; the next seal is checked against the purged run's last hash), and a problem otherwise.
    /// <paramref name="fromChainSeq"/> (tests) checks from that seal on; only a full check is recorded on
    /// web.audit_chain_state.
    /// </summary>
    public async Task<AuditChainVerifyResult> VerifyAsync(CancellationToken cancellationToken = default, long? fromChainSeq = null)
    {
        await SealAllAsync();
        using var connection = connectionFactory.Create();
        var state = await connection.QuerySingleAsync<(long LastChainSeq, DateTime? LastSealedAt)>(
            "SELECT LastChainSeq, LastSealedAt FROM web.audit_chain_state WHERE AuditChainStateKey = 1");
        var purgedByEnd = (await connection.QueryAsync<(long FromChainSeq, long ToChainSeq, byte[] ToChainHash)>(
            "SELECT FromChainSeq, ToChainSeq, ToChainHash FROM web.audit_chain_purged")).ToDictionary(r => r.ToChainSeq);

        var problems = new List<AuditChainProblem>();
        var more = false;
        void Problem(long? seq, long? eventKey, string kind)
        {
            if (problems.Count < MaxProblemsListed) problems.Add(new AuditChainProblem(seq, eventKey, kind));
            else more = true;
        }

        // The hash a seal after a gap (previousSeq+1 .. nextSeq-1) was made after, if purges deleted exactly that run.
        byte[]? PurgedRunHash(long previousSeq, long nextSeq)
        {
            if (!purgedByEnd.TryGetValue(nextSeq - 1, out var last)) return null;
            var run = last;
            while (run.FromChainSeq > previousSeq + 1)
            {
                if (!purgedByEnd.TryGetValue(run.FromChainSeq - 1, out run)) return null;
            }
            return run.FromChainSeq == previousSeq + 1 ? last.ToChainHash : null;
        }

        long previousSeq;
        byte[]? previousHash;
        if (fromChainSeq is { } from && from > 1)
        {
            previousSeq = from - 1;
            previousHash = await connection.QuerySingleOrDefaultAsync<byte[]?>(
                "SELECT ChainHash FROM web.audit_chain WHERE ChainSeq = @previousSeq", new { previousSeq });
        }
        else
        {
            previousSeq = 0;
            previousHash = null;
        }
        var canCheck = fromChainSeq is null or <= 1 || previousHash is not null;

        long? first = null;
        var checkedCount = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var seals = (await connection.QueryAsync<SealRow>("""
                SELECT TOP (@batch) ChainSeq, AuditEventKey, AuditChainKeyId, ChainHash FROM web.audit_chain
                WHERE ChainSeq > @after ORDER BY ChainSeq
                """, new { batch = VerifyBatchSize, after = previousSeq })).AsList();
            if (seals.Count == 0) break;
            var keys = seals.Select(s => s.AuditEventKey).ToList();
            var events = (await ReadEventsAsync(connection, null, """
                SELECT AuditEventKey, AuditEventTypeKey, OccurredAt, PerformedByUserKey, EntityName, EntityKey, SourceIpAddress, Detail, Reason
                FROM web.audit_event WHERE AuditEventKey IN @keys
                """, new { keys })).ToDictionary(e => e.AuditEventKey);

            foreach (var seal in seals)
            {
                first ??= seal.ChainSeq;
                checkedCount++;
                if (seal.ChainSeq != previousSeq + 1)
                {
                    previousHash = PurgedRunHash(previousSeq, seal.ChainSeq);
                    canCheck = previousHash is not null;
                    if (!canCheck) Problem(previousSeq + 1, null, "SealMissing");
                }
                if (!events.TryGetValue(seal.AuditEventKey, out var e))
                {
                    Problem(seal.ChainSeq, seal.AuditEventKey, "EventDeleted");
                }
                else if (canCheck)
                {
                    var expected = AuditChainHasher.Compute(await KeyAsync(seal.AuditChainKeyId), previousHash, e);
                    if (!CryptographicOperations.FixedTimeEquals(expected, seal.ChainHash)) Problem(seal.ChainSeq, seal.AuditEventKey, "Edited");
                }
                previousSeq = seal.ChainSeq;
                previousHash = seal.ChainHash;
                canCheck = true;
            }
        }
        if (previousSeq < state.LastChainSeq && PurgedRunHash(previousSeq, state.LastChainSeq + 1) is null)
            Problem(previousSeq + 1, null, "SealsMissingAtEnd");

        // Every event at or below LookedAtVersion has been sealed, so only those above it can be waiting (an index range).
        var unsealed = await connection.QuerySingleAsync<int>("""
            SELECT COUNT(*) FROM web.audit_event ae WITH (INDEX(IX_audit_event_WriteVersion))
            WHERE ae.WriteVersion > (SELECT LookedAtVersion FROM web.audit_chain_state WHERE AuditChainStateKey = 1)
              AND NOT EXISTS (SELECT 1 FROM web.audit_chain c WHERE c.AuditEventKey = ae.AuditEventKey)
            """);
        var result = new AuditChainVerifyResult(problems.Count == 0, checkedCount, first, checkedCount == 0 ? null : previousSeq,
            unsealed, state.LastSealedAt, problems, more);
        if (fromChainSeq is null)
        {
            await connection.ExecuteAsync("""
                UPDATE web.audit_chain_state SET LastVerifiedAt = SYSUTCDATETIME(), LastVerifyIntact = @intact, LastVerifySummary = @summary
                WHERE AuditChainStateKey = 1
                """, new { intact = result.Intact, summary = result.Summary });
        }
        return result;
    }
}
