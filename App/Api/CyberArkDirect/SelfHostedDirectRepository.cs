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
    public int? SafeCount { get; init; }
    public int? AccountCount { get; init; }
    public int? UserCount { get; init; }
    public int? GroupCount { get; init; }
    public int? MemberCount { get; init; }
    public int? PlatformCount { get; init; }
    public string? Message { get; init; }

    /// <summary>
    /// D-250: the Safe and account counts this pull was compared with, so a rejected row can show "40 to 10": the ones
    /// stored with the pull (D-277), else the last pull that Succeeded before it.
    /// </summary>
    public int? BaselineSafeCount { get; init; }
    public int? BaselineAccountCount { get; init; }
}

/// <summary>D-248: the pull log, and the one place that writes the stg_sh_* tables from a pull.</summary>
public sealed class SelfHostedDirectRepository(IDbConnectionFactory connectionFactory)
{
    /// <summary>A pull still "Started" after this long died with the app (a restart, a crash); it is marked Failed so it can't block the next one.</summary>
    private static readonly TimeSpan StaleStart = TimeSpan.FromHours(2);

    private const string Columns = "PullKey, StartedAt, CompletedAt, Status, TriggeredBy, AcceptedChange, SafeCount, AccountCount, UserCount, GroupCount, MemberCount, PlatformCount, Message";

    public async Task<long> StartPullAsync(string triggeredBy, bool acceptedChange)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("""
            UPDATE dbo.cyberark_direct_pull
            SET Status = N'Failed', CompletedAt = SYSUTCDATETIME(), Message = N'The pull never finished (the application stopped or restarted).'
            WHERE Status = N'Started' AND StartedAt < DATEADD(SECOND, -@seconds, SYSUTCDATETIME())
            """, new { seconds = (int)StaleStart.TotalSeconds });
        return await connection.ExecuteScalarAsync<long>("""
            INSERT INTO dbo.cyberark_direct_pull (TriggeredBy, AcceptedChange) VALUES (@triggeredBy, @acceptedChange);
            SELECT CAST(SCOPE_IDENTITY() AS BIGINT);
            """, new { triggeredBy, acceptedChange });
    }

    public async Task CompleteAsync(long pullKey, string status, PullCounts? counts, string? message, Guid? batchId, PullCounts? baseline = null)
    {
        using var connection = connectionFactory.Create();
        if (baseline is not null)
            await connection.ExecuteAsync(
                "UPDATE dbo.cyberark_direct_pull SET BaselineSafeCount = @SafeCount, BaselineAccountCount = @AccountCount WHERE PullKey = @pullKey",
                new { pullKey, baseline.SafeCount, baseline.AccountCount });
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

    /// <summary>The last pull that Succeeded; the next one's counts are compared with it.</summary>
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
            SELECT TOP (@count) p.PullKey, p.StartedAt, p.CompletedAt, p.Status, p.TriggeredBy, p.AcceptedChange, p.SafeCount, p.AccountCount,
                   p.UserCount, p.GroupCount, p.MemberCount, p.PlatformCount, p.Message,
                   COALESCE(p.BaselineSafeCount, b.SafeCount) AS BaselineSafeCount, COALESCE(p.BaselineAccountCount, b.AccountCount) AS BaselineAccountCount
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
        // D-277: CyberArk's internal Safes are left out; PVWA never returns them, so they would always look gone.
        var rows = await connection.QueryAsync<StagedSafe>("""
            SELECT s.CASSafeID AS Number, s.CASSafeName AS Name FROM dbo.stg_sh_safes s
            WHERE s.CASSafeName IS NOT NULL
              AND NOT EXISTS (SELECT 1 FROM dbo.vw_selfhosted_internal_safe i JOIN dbo.dim_safe d ON d.SafeKey = i.SafeKey
                              WHERE d.SafeUrlId = CAST(s.CASSafeID AS NVARCHAR(300)))
            """);
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

    /// <summary>D-281: the names of the Privilege Cloud groups that are a member of some Safe (as of the last load).</summary>
    public async Task<IReadOnlyList<string>> GetPrivilegeCloudSafeMemberGroupsAsync()
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<string>("""
            SELECT DISTINCT g.GroupName
            FROM dbo.fact_safe_entitlement fse
            JOIN dbo.dim_group g ON g.GroupKey = fse.GroupKey
            JOIN dbo.dim_source_system s ON s.SourceSystemKey = g.SourceSystemKey AND s.SourceSystemCode = 'PRIVCLOUD'
            WHERE fse.MemberType = 'Group' AND g.GroupName IS NOT NULL
            ORDER BY g.GroupName
            """)).ToList();
    }

    /// <summary>
    /// D-281: the standalone AD read (Self-Hosted off): replaces the staged AD reads with these and applies them at once
    /// (dbo.usp_Apply_AdGroupMembers), in one transaction. The nightly Load's own AD step needs Direct staging, so it
    /// leaves these alone.
    /// </summary>
    public async Task ApplyAdGroupsAsync(StagingTables tables, CancellationToken ct)
    {
        using var raw = connectionFactory.Create();
        var connection = (SqlConnection)raw;
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);
        foreach (var data in new[] { tables.DirectAdGroupReads, tables.DirectAdGroupMembers })
        {
            await using (var delete = new SqlCommand($"DELETE FROM dbo.{data.TableName}", connection, transaction)) await delete.ExecuteNonQueryAsync(ct);
            if (data.Rows.Count == 0) continue;
            using var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction) { DestinationTableName = "dbo." + data.TableName, BulkCopyTimeout = 600 };
            foreach (DataColumn column in data.Columns) bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            await bulk.WriteToServerAsync(data, ct);
        }
        await using (var apply = new SqlCommand("EXEC dbo.usp_Apply_AdGroupMembers", connection, transaction) { CommandTimeout = 600 }) await apply.ExecuteNonQueryAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>D-280: the classic account details read by earlier pulls (dbo.cyberark_direct_account_detail), by account id.</summary>
    public async Task<IReadOnlyDictionary<string, PvwaAccountDetail>> GetAccountDetailCacheAsync()
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<PvwaAccountDetail>("""
            SELECT AccountId, CategoryModificationTime, DeviceType, CreationMethod, LogonSafe, LogonFolder, LogonName,
                   ReconcileSafe, ReconcileFolder, ReconcileName
            FROM dbo.cyberark_direct_account_detail
            """);
        return rows.ToDictionary(r => r.AccountId, StringComparer.Ordinal);
    }

    /// <summary>D-280: adds or replaces the cached details of the accounts just read.</summary>
    public async Task UpsertAccountDetailCacheAsync(IReadOnlyCollection<PvwaAccountDetail> details)
    {
        using var connection = connectionFactory.Create();
        foreach (var chunk in details.Chunk(500))
        {
            await connection.ExecuteAsync("""
                MERGE dbo.cyberark_direct_account_detail AS t
                USING (SELECT @AccountId AS AccountId) AS s ON t.AccountId = s.AccountId
                WHEN MATCHED THEN UPDATE SET CategoryModificationTime = @CategoryModificationTime, DeviceType = @DeviceType,
                    CreationMethod = @CreationMethod, LogonSafe = @LogonSafe, LogonFolder = @LogonFolder, LogonName = @LogonName,
                    ReconcileSafe = @ReconcileSafe, ReconcileFolder = @ReconcileFolder, ReconcileName = @ReconcileName, ReadAt = SYSUTCDATETIME()
                WHEN NOT MATCHED THEN INSERT (AccountId, CategoryModificationTime, DeviceType, CreationMethod, LogonSafe, LogonFolder, LogonName,
                    ReconcileSafe, ReconcileFolder, ReconcileName)
                    VALUES (@AccountId, @CategoryModificationTime, @DeviceType, @CreationMethod, @LogonSafe, @LogonFolder, @LogonName,
                    @ReconcileSafe, @ReconcileFolder, @ReconcileName);
                """, chunk.Select(Fit));
        }
    }

    /// <summary>D-280: forgets accounts the staged pull no longer has (deleted in CyberArk).</summary>
    public async Task PruneAccountDetailCacheAsync()
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("""
            DELETE c FROM dbo.cyberark_direct_account_detail c
            WHERE EXISTS (SELECT 1 FROM dbo.stg_shd_accounts)
              AND NOT EXISTS (SELECT 1 FROM dbo.stg_shd_accounts a WHERE a.AccountId = c.AccountId)
            """);
    }

    private static PvwaAccountDetail Fit(PvwaAccountDetail d) => d with
    {
        DeviceType = Cut(d.DeviceType, 100), CreationMethod = Cut(d.CreationMethod, 100),
        LogonSafe = Cut(d.LogonSafe, 300), LogonFolder = Cut(d.LogonFolder, 300), LogonName = Cut(d.LogonName, 512),
        ReconcileSafe = Cut(d.ReconcileSafe, 300), ReconcileFolder = Cut(d.ReconcileFolder, 300), ReconcileName = Cut(d.ReconcileName, 512)
    };

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    /// <summary>
    /// Replaces the Self-Hosted staging tables with the pull's rows, in one transaction, so staging is never half
    /// filled: the EVD-shaped stg_sh_* tables the Load reads, and (D-270) the stg_shd_* tables in REST's shape.
    /// DELETE, not TRUNCATE: the app's database account has no ALTER (Database/Manual/02).
    /// The Requests and Confirmations tables (dual-control workflow) have no REST source and are emptied.
    /// </summary>
    public async Task StageAsync(StagingTables tables, CancellationToken ct)
    {
        using var raw = connectionFactory.Create();
        var connection = (SqlConnection)raw;
        await connection.OpenAsync(ct);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(ct);

        string[] evdShaped = ["stg_sh_users", "stg_sh_groups", "stg_sh_groupmembers", "stg_sh_safes", "stg_sh_owners", "stg_sh_files",
                              "stg_sh_objectproperties", "stg_sh_requests", "stg_sh_confirmations", "stg_sh_platforms"];
        foreach (var table in evdShaped.Concat(tables.Direct.Select(t => t.TableName)))
        {
            await using var delete = new SqlCommand($"DELETE FROM dbo.{table}", connection, transaction) { CommandTimeout = 600 };
            await delete.ExecuteNonQueryAsync(ct);
        }

        foreach (var data in tables.All)
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
