using Dapper;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.CyberArkDirect;

/// <summary>One row of dbo.shd_comparison_run (42_BlueTrack_SelfHostedShadowPull.sql): the counts of one compared shadow pull.</summary>
public sealed class ShadowComparisonRun
{
    public long RunKey { get; init; }
    public long PullKey { get; init; }
    public DateTime ComparedAt { get; init; }
    public DateTime? EvdStagedAt { get; init; }
    public bool EvdUsable { get; init; }
    public string? EvdNote { get; init; }
    public int MissingFromDirect { get; init; }
    public int MissingExplained { get; init; }
    public int OnlyInDirect { get; init; }
    public int AccountFieldDiffs { get; init; }
    public int AccountFieldExplained { get; init; }
    public int PermissionDiffs { get; init; }
    public int LogonDomainDiffs { get; init; }
    public int BlockingCount { get; init; }
    public bool IsClean { get; init; }
}

/// <summary>One row of dbo.shd_comparison_difference: a difference found by the latest comparison.</summary>
public sealed class ShadowComparisonDifference
{
    public long DifferenceKey { get; init; }
    public string Category { get; init; } = "";
    public string ItemKind { get; init; } = "";
    public string ItemKey { get; init; } = "";
    public string? ItemName { get; init; }
    public string? SafeName { get; init; }
    public string? Field { get; init; }
    public string? EvdValue { get; init; }
    public string? DirectValue { get; init; }
    public string? MarkCategory { get; init; }
    /// <summary>D-272: the mark is one of the comparison's own rules (an internal Safe, a PVWA gateway member), not an admin's.</summary>
    public bool MarkAuto { get; init; }
    public bool Blocking { get; init; }
    public long? SafeNumber { get; init; }
    public long? PrincipalId { get; init; }
}

/// <summary>
/// A Safe the latest comparison found PVWA can't fully see: not returned at all (the PVWA user can't see it, e.g. a Safe
/// whose members were customised), or returned with accounts or members missing (it lacks List Accounts or View Safe
/// Members there). <see cref="AnchorDifferenceKey"/> is one of its differences, to mark the whole Safe by.
/// </summary>
public sealed class ShadowSafeVisibility
{
    public long SafeNumber { get; init; }
    public string? SafeName { get; init; }
    public bool NotReturned { get; init; }
    public int AccountsMissing { get; init; }
    public int MembersMissing { get; init; }
    public bool Blocking { get; init; }
    public string? MarkCategory { get; init; }
    public bool MarkAuto { get; init; }
    public long AnchorDifferenceKey { get; init; }
}

/// <summary>One row of web.shd_comparison_mark: a difference an admin marked as expected.</summary>
public sealed class ShadowComparisonMark
{
    public int MarkKey { get; init; }
    public string ItemKind { get; init; } = "";
    public string ItemKey { get; init; } = "";
    public string? ItemName { get; init; }
    public string Category { get; init; } = "";
    public string? Note { get; init; }
    public string? MarkedByName { get; init; }
    public DateTime MarkedAt { get; init; }
}

/// <summary>
/// D-271: the shadow comparison of the EVD-staged stg_sh_* tables with a shadow pull's stg_shd_* tables. The SQL does
/// the comparing (dbo.vw_shd_comparison, dbo.usp_Compare_SelfHostedShadow); this stores, reads and marks.
/// </summary>
public sealed class ShadowComparisonRepository(IDbConnectionFactory connectionFactory)
{
    public static readonly string[] ItemKinds = ["Safe", "Account", "User", "Group", "Member"];
    public static readonly string[] MarkCategories = ["Internal", "Expected"];
    public static readonly string[] DifferenceCategories = ["MissingFromDirect", "OnlyInDirect", "AccountField", "Permission", "LogonDomain"];

    /// <summary>Compares what is staged now and stores it as the comparison of the shadow pull <paramref name="pullKey"/>.</summary>
    public async Task<long> CompareAsync(long pullKey, CancellationToken ct = default)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("dbo.usp_Compare_SelfHostedShadow", new { PullKey = pullKey },
            commandType: System.Data.CommandType.StoredProcedure, commandTimeout: 600, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ShadowComparisonRun>> GetRunsAsync(int count)
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<ShadowComparisonRun>("""
            SELECT TOP (@count) RunKey, PullKey, ComparedAt, EvdStagedAt, EvdUsable, EvdNote, MissingFromDirect, MissingExplained, OnlyInDirect,
                   AccountFieldDiffs, AccountFieldExplained, PermissionDiffs, LogonDomainDiffs, BlockingCount, IsClean
            FROM dbo.shd_comparison_run
            ORDER BY RunKey DESC
            """, new { count })).ToList();
    }

    /// <summary>The latest comparison's differences, blocking ones first, at most <paramref name="max"/>, and how many there are in all.</summary>
    public async Task<(IReadOnlyList<ShadowComparisonDifference> Rows, int Total)> GetDifferencesAsync(string? category, bool blockingOnly, int max)
    {
        using var connection = connectionFactory.Create();
        const string where = "WHERE (@category IS NULL OR Category = @category) AND (@blockingOnly = 0 OR Blocking = 1)";
        var parameters = new { category, blockingOnly, max };
        var total = await connection.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM dbo.shd_comparison_difference {where}", parameters);
        var rows = await connection.QueryAsync<ShadowComparisonDifference>($"""
            SELECT TOP (@max) DifferenceKey, Category, ItemKind, ItemKey, ItemName, SafeName, Field, EvdValue, DirectValue, MarkCategory, MarkAuto, Blocking,
                   SafeNumber, PrincipalId
            FROM dbo.shd_comparison_difference
            {where}
            ORDER BY Blocking DESC, Category, ItemKind, SafeName, ItemName, Field
            """, parameters);
        return (rows.ToList(), total);
    }

    /// <summary>D-271: the Safes the latest comparison found PVWA can't fully see, ones it didn't return first.</summary>
    public async Task<IReadOnlyList<ShadowSafeVisibility>> GetSafeVisibilityAsync()
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<ShadowSafeVisibility>("""
            SELECT SafeNumber, MAX(SafeName) AS SafeName,
                   CAST(MAX(CASE WHEN ItemKind = N'Safe' THEN 1 ELSE 0 END) AS BIT) AS NotReturned,
                   COUNT(CASE WHEN ItemKind = N'Account' THEN 1 END) AS AccountsMissing,
                   COUNT(CASE WHEN ItemKind = N'Member' THEN 1 END) AS MembersMissing,
                   CAST(MAX(CASE WHEN Blocking = 1 THEN 1 ELSE 0 END) AS BIT) AS Blocking,
                   MAX(CASE WHEN ItemKind = N'Safe' THEN MarkCategory END) AS MarkCategory,
                   CAST(ISNULL(MAX(CASE WHEN ItemKind = N'Safe' THEN CAST(MarkAuto AS INT) END), 0) AS BIT) AS MarkAuto,
                   MIN(DifferenceKey) AS AnchorDifferenceKey
            FROM dbo.shd_comparison_difference
            WHERE Category = N'MissingFromDirect' AND SafeNumber IS NOT NULL
            GROUP BY SafeNumber
            ORDER BY NotReturned DESC, SafeName
            """)).ToList();
    }

    public async Task<ShadowComparisonDifference?> GetDifferenceAsync(long differenceKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<ShadowComparisonDifference>("""
            SELECT DifferenceKey, Category, ItemKind, ItemKey, ItemName, SafeName, Field, EvdValue, DirectValue, MarkCategory, MarkAuto, Blocking, SafeNumber, PrincipalId
            FROM dbo.shd_comparison_difference WHERE DifferenceKey = @differenceKey
            """, new { differenceKey });
    }

    /// <summary>Whether an id is a group's in either source (users and groups share the Vault's id space).</summary>
    public async Task<bool> IsGroupAsync(long id)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteScalarAsync<int>("""
            SELECT CASE WHEN EXISTS (SELECT 1 FROM dbo.stg_sh_groups WHERE CAGGroupID = @id)
                          OR EXISTS (SELECT 1 FROM dbo.stg_shd_groups WHERE GroupId = @id) THEN 1 ELSE 0 END
            """, new { id }) == 1;
    }

    public async Task<IReadOnlyList<ShadowComparisonMark>> GetMarksAsync()
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<ShadowComparisonMark>("""
            SELECT m.MarkKey, m.ItemKind, m.ItemKey, m.ItemName, m.Category, m.Note, u.DisplayName AS MarkedByName, m.MarkedAt
            FROM web.shd_comparison_mark m
            LEFT JOIN web.app_user u ON u.UserKey = m.MarkedBy
            ORDER BY m.ItemKind, m.ItemName, m.ItemKey
            """)).ToList();
    }

    /// <summary>Adds a mark, or replaces the category and note of the same item's mark, then re-applies the marks.</summary>
    public async Task<int> SaveMarkAsync(string itemKind, string itemKey, string? itemName, string category, string? note, int userKey)
    {
        using var connection = connectionFactory.Create();
        var markKey = await connection.ExecuteScalarAsync<int>("""
            MERGE web.shd_comparison_mark WITH (HOLDLOCK) AS t
            USING (SELECT @itemKind AS ItemKind, @itemKey AS ItemKey) AS s ON t.ItemKind = s.ItemKind AND t.ItemKey = s.ItemKey
            WHEN MATCHED THEN UPDATE SET ItemName = @itemName, Category = @category, Note = @note, MarkedBy = @userKey, MarkedAt = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN INSERT (ItemKind, ItemKey, ItemName, Category, Note, MarkedBy)
                VALUES (@itemKind, @itemKey, @itemName, @category, @note, @userKey)
            OUTPUT inserted.MarkKey;
            """, new { itemKind, itemKey, itemName, category, note, userKey });
        await connection.ExecuteAsync("dbo.usp_Apply_ShadowComparisonMarks", commandType: System.Data.CommandType.StoredProcedure);
        return markKey;
    }

    public async Task<ShadowComparisonMark?> DeleteMarkAsync(int markKey)
    {
        using var connection = connectionFactory.Create();
        var mark = await connection.QuerySingleOrDefaultAsync<ShadowComparisonMark>("""
            DELETE FROM web.shd_comparison_mark
            OUTPUT deleted.MarkKey, deleted.ItemKind, deleted.ItemKey, deleted.ItemName, deleted.Category, deleted.Note, deleted.MarkedAt
            WHERE MarkKey = @markKey
            """, new { markKey });
        if (mark is not null)
            await connection.ExecuteAsync("dbo.usp_Apply_ShadowComparisonMarks", commandType: System.Data.CommandType.StoredProcedure);
        return mark;
    }
}

/// <summary>D-271: the cutover rule. Production may switch to Direct after this many clean nights in a row (agreed 2026-10-20).</summary>
public static class ShadowCleanNights
{
    public const int Needed = 7;

    /// <summary>
    /// How many nights in a row, ending last night or tonight, had a clean comparison. A night is judged by its latest
    /// comparison (a Pull now may compare again); a night with no comparison ends the run, as does one that wasn't clean.
    /// </summary>
    public static int Count(IEnumerable<ShadowComparisonRun> runs, DateTime todayLocal, TimeZoneInfo zone)
    {
        var byNight = runs
            .GroupBy(r => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(r.ComparedAt, DateTimeKind.Utc), zone).Date)
            .ToDictionary(g => g.Key, g => g.MaxBy(r => r.RunKey)!.IsClean);

        var night = todayLocal.Date;
        if (!byNight.ContainsKey(night)) night = night.AddDays(-1);   // tonight's pull hasn't run yet
        var count = 0;
        while (byNight.TryGetValue(night, out var clean) && clean)
        {
            count++;
            night = night.AddDays(-1);
        }
        return count;
    }
}
