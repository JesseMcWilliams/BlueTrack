using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.CyberArkDirect;

/// <summary>One row of dbo.cyberark_direct_pull (38_BlueTrack_SelfHostedDirect.sql).</summary>
public sealed class DirectPullRecord
{
    public long PullKey { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? CompletedAt { get; init; }
    public string Status { get; init; } = "";
    public string TriggeredBy { get; init; } = "";
    public bool AcceptedChange { get; init; }
    /// <summary>D-271: a shadow pull, made with the EVD as the source: it wrote only the stg_shd_* tables and never gates the import.</summary>
    public bool IsShadow { get; init; }
    public int? SafeCount { get; init; }
    public int? AccountCount { get; init; }
    public int? UserCount { get; init; }
    public int? GroupCount { get; init; }
    public int? MemberCount { get; init; }
    public int? PlatformCount { get; init; }
    public string? Message { get; init; }

    /// <summary>D-250: the Safe and account counts of the last pull that Succeeded before this one, so a rejected row can show "40 to 10".</summary>
    public int? BaselineSafeCount { get; init; }
    public int? BaselineAccountCount { get; init; }
}

/// <summary>D-248: the pull log, and the one place that writes the stg_sh_* tables from a pull.</summary>
public sealed class SelfHostedDirectRepository(IDbConnectionFactory connectionFactory)
{
    /// <summary>A pull still "Started" after this long died with the app (a restart, a crash); it is marked Failed so it can't block the next one.</summary>
    private static readonly TimeSpan StaleStart = TimeSpan.FromHours(2);

    private const string Columns = "PullKey, StartedAt, CompletedAt, Status, TriggeredBy, AcceptedChange, IsShadow, SafeCount, AccountCount, UserCount, GroupCount, MemberCount, PlatformCount, Message";

    public async Task<long> StartPullAsync(string triggeredBy, bool acceptedChange, bool isShadow = false)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("""
            UPDATE dbo.cyberark_direct_pull
            SET Status = N'Failed', CompletedAt = SYSUTCDATETIME(), Message = N'The pull never finished (the application stopped or restarted).'
            WHERE Status = N'Started' AND StartedAt < DATEADD(SECOND, -@seconds, SYSUTCDATETIME())
            """, new { seconds = (int)StaleStart.TotalSeconds });
        return await connection.ExecuteScalarAsync<long>("""
            INSERT INTO dbo.cyberark_direct_pull (TriggeredBy, AcceptedChange, IsShadow) VALUES (@triggeredBy, @acceptedChange, @isShadow);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
            """, new { triggeredBy, acceptedChange, isShadow });
    }

    public async Task CompleteAsync(long pullKey, string status, PullCounts? counts, string? message, Guid? batchId)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("""
            UPDATE dbo.cyberark_direct_pull
            SET Status = @status, CompletedAt = SYSUTCDATETIME(), ImportBatchId = @batchId, Message = @message,
                SafeCount = @SafeCount, AccountCount = @AccountCount, UserCount = @UserCount, GroupCount = @GroupCount,
                MemberCount = @MemberCount, PlatformCount = @PlatformCount
            WHERE PullKey = @pullKey
            """, new
        {
            pullKey, status, batchId, message = message is { Length: > 2000 } ? message[..2000] : message,
            counts?.SafeCount, counts?.AccountCount, counts?.UserCount, counts?.GroupCount, counts?.MemberCount, counts?.PlatformCount
        });
    }

    /// <summary>
    /// The last pull that Succeeded; the next one's counts are compared with it. A shadow pull counts (D-271): it read the
    /// same PVWA the same way, so the first real pull after a shadow period is checked against it.
    /// </summary>
    public async Task<DirectPullRecord?> GetLastSucceededAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QueryFirstOrDefaultAsync<DirectPullRecord>(
            $"SELECT TOP (1) {Columns} FROM dbo.cyberark_direct_pull WHERE Status = N'Succeeded' ORDER BY PullKey DESC");
    }

    public async Task<IReadOnlyList<DirectPullRecord>> GetRecentAsync(int count)
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<DirectPullRecord>($"""
            SELECT TOP (@count) p.PullKey, p.StartedAt, p.CompletedAt, p.Status, p.TriggeredBy, p.AcceptedChange, p.IsShadow, p.SafeCount, p.AccountCount,
                   p.UserCount, p.GroupCount, p.MemberCount, p.PlatformCount, p.Message,
                   b.SafeCount AS BaselineSafeCount, b.AccountCount AS BaselineAccountCount
            FROM dbo.cyberark_direct_pull p
            OUTER APPLY (SELECT TOP (1) SafeCount, AccountCount FROM dbo.cyberark_direct_pull
                         WHERE Status = N'Succeeded' AND PullKey < p.PullKey ORDER BY PullKey DESC) b
            ORDER BY p.PullKey DESC
            """, new { count })).ToList();
    }

    /// <summary>The Safes the last good pull staged (a rejected or failed pull leaves staging alone), for naming what disappeared.</summary>
    public async Task<IReadOnlyList<(long Number, string Name)>> GetStagedSafesAsync()
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<StagedSafe>("SELECT CASSafeID AS Number, CASSafeName AS Name FROM dbo.stg_sh_safes WHERE CASSafeName IS NOT NULL");
        return rows.Select(r => (r.Number, r.Name)).ToList();
    }

    private sealed class StagedSafe
    {
        public long Number { get; init; }
        public string Name { get; init; } = "";
    }

    public async Task<int> PurgeAsync(int keepDays)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync(
            "DELETE FROM dbo.cyberark_direct_pull WHERE StartedAt < DATEADD(DAY, -@keepDays, SYSUTCDATETIME()) AND PullKey < (SELECT MAX(PullKey) FROM dbo.cyberark_direct_pull)",
            new { keepDays });
    }

    /// <summary>dim_location's Self-Hosted rows with the EVD's numeric id, so a location keeps its number when a pull replaces the EVD import.</summary>
    public async Task<IReadOnlyDictionary<string, long>> GetKnownLocationsAsync()
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<KnownLocation>("""
            SELECT l.LocationName, TRY_CAST(l.SourceLocationId AS BIGINT) AS Id
            FROM dim_location l
            JOIN dim_source_system s ON s.SourceSystemKey = l.SourceSystemKey AND s.SourceSystemCode = 'SELFHOSTED'
            WHERE TRY_CAST(l.SourceLocationId AS BIGINT) IS NOT NULL AND l.LocationName IS NOT NULL
            """);
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows) map.TryAdd(row.LocationName, row.Id);
        return map;
    }

    // A class, not a tuple: Dapper doesn't map query results onto a ValueTuple.
    private sealed class KnownLocation
    {
        public string LocationName { get; init; } = "";
        public long Id { get; init; }
    }

    /// <summary>
    /// Replaces the Self-Hosted staging tables with the pull's rows, in one transaction, so staging is never half
    /// filled: the EVD-shaped stg_sh_* tables the Load reads, and (D-270) the stg_shd_* tables in REST's shape.
    /// DELETE, not TRUNCATE: the app's database account has no ALTER (Database/Manual/02).
    /// The Requests and Confirmations tables (dual-control workflow) have no REST source and are emptied.
    /// D-271: a shadow pull (<paramref name="directOnly"/>) replaces only the stg_shd_* tables; the EVD-staged stg_sh_*
    /// tables, which the Load reads, are left alone.
    /// </summary>
    public async Task StageAsync(StagingTables tables, CancellationToken ct, bool directOnly = false)
    {
        using var raw = connectionFactory.Create();
        var connection = (SqlConnection)raw;
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        string[] evdShaped = ["stg_sh_users", "stg_sh_groups", "stg_sh_groupmembers", "stg_sh_safes", "stg_sh_owners", "stg_sh_files",
                              "stg_sh_objectproperties", "stg_sh_requests", "stg_sh_confirmations", "stg_sh_platforms"];
        foreach (var table in (directOnly ? [] : evdShaped).Concat(tables.Direct.Select(t => t.TableName)))
        {
            await using var delete = new SqlCommand($"DELETE FROM dbo.{table}", connection, transaction) { CommandTimeout = 600 };
            await delete.ExecuteNonQueryAsync(ct);
        }

        foreach (var data in directOnly ? tables.Direct : tables.All)
        {
            if (data.Rows.Count == 0) continue;
            using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
            {
                DestinationTableName = "dbo." + data.TableName, BulkCopyTimeout = 600, BatchSize = 5000
            };
            foreach (DataColumn column in data.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            await bulk.WriteToServerAsync(data, ct);
        }
        await transaction.CommitAsync(ct);
    }
}

/// <summary>What a pull counted; Safes and Accounts are what the change guard compares.</summary>
public sealed record PullCounts(int SafeCount, int AccountCount, int UserCount, int GroupCount, int MemberCount, int PlatformCount)
{
    public static PullCounts Of(PvwaSnapshot s) => new(s.Safes.Count, s.Accounts.Count, s.Users.Count, s.Groups.Count, s.Members.Count, s.Platforms.Count);
}
