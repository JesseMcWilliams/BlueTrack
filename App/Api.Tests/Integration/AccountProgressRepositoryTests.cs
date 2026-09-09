using BlueTrack.Api.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// Covers D-42's sort-column whitelist (AccountProgressRepository's own
/// SortableColumns) against a real SQL Server connection -- SortParser
/// itself does no validation (see SortParserTests' own note on that
/// boundary); this is where an unrecognized or malicious "sort by" field
/// actually gets stopped before reaching a raw SQL ORDER BY clause.
/// </summary>
public class AccountProgressRepositoryTests
{
    [Fact]
    public async Task GetSummaryListAsync_NoSort_DoesNotThrow()
    {
        var repository = new AccountProgressRepository(new TestDbConnectionFactory());

        var results = await repository.GetSummaryListAsync();

        Assert.NotNull(results);
    }

    [Fact]
    public async Task GetSummaryListAsync_KnownSortField_DoesNotThrow()
    {
        var repository = new AccountProgressRepository(new TestDbConnectionFactory());

        var results = await repository.GetSummaryListAsync(sortBy: [("ownerName", true)]);

        Assert.NotNull(results);
    }

    [Fact]
    public async Task GetSummaryListAsync_SqlInjectionAttemptAsSortField_IsIgnoredNotExecuted()
    {
        var repository = new AccountProgressRepository(new TestDbConnectionFactory());

        // If this ever reached the SQL text as a raw column reference,
        // SQL Server would throw a syntax error (or worse, actually
        // execute it) -- the whitelist should just drop it silently and
        // fall back to the default ORDER BY, so this must complete cleanly.
        var results = await repository.GetSummaryListAsync(
            sortBy: [("AccountName; DROP TABLE dbo.fact_account_progress; --", false)]);

        Assert.NotNull(results);
    }

    /// <summary>
    /// D-124 Phase 3: page/pageSize paging -- creates 3 fact_account/
    /// fact_account_progress rows sharing a unique OwnerName substring (for
    /// a race-free isolated view via the existing ownerContains filter,
    /// since GetSummaryListAsync's default sort spans the whole table and
    /// other tests run concurrently against it) and confirms page 1/page 2
    /// are disjoint, correctly-ordered (accountName ascending, the default
    /// sort) slices.
    /// </summary>
    [Fact]
    public async Task GetSummaryListAsync_PageAndPageSize_ReturnsDisjointCorrectlyOrderedPages()
    {
        var repository = new AccountProgressRepository(new TestDbConnectionFactory());
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var ownerName = $"PageTestOwner-{suffix}";

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        var discoveredStageKey = await connection.QuerySingleAsync<int>("SELECT StageKey FROM dbo.dim_blueprint_stage WHERE StageName = 'Discovered'");
        var notStartedStatusKey = await connection.QuerySingleAsync<int>("SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = 'Not Started'");

        var names = new[] { $"PageTest-{suffix}-A", $"PageTest-{suffix}-B", $"PageTest-{suffix}-C" };
        var accountKeys = new List<long>();
        foreach (var name in names)
        {
            var accountKey = await connection.QuerySingleAsync<long>(
                "INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted) OUTPUT inserted.AccountKey VALUES (1, @SourceAccountId, @AccountName, 0)",
                new { SourceAccountId = $"IntegrationTest_{Guid.NewGuid():N}", AccountName = name });
            await connection.ExecuteAsync(
                "INSERT INTO fact_account_progress (AccountKey, CurrentStageKey, CurrentStatusKey, OwnerName) VALUES (@AccountKey, @StageKey, @StatusKey, @OwnerName)",
                new { AccountKey = accountKey, StageKey = discoveredStageKey, StatusKey = notStartedStatusKey, OwnerName = ownerName });
            accountKeys.Add(accountKey);
        }

        try
        {
            var page1 = await repository.GetSummaryListAsync(ownerContains: ownerName, page: 1, pageSize: 2);
            var page2 = await repository.GetSummaryListAsync(ownerContains: ownerName, page: 2, pageSize: 2);

            Assert.Equal(2, page1.Count);
            Assert.Single(page2);
            Assert.Equal(names[0], page1[0].AccountName);
            Assert.Equal(names[1], page1[1].AccountName);
            Assert.Equal(names[2], page2[0].AccountName);
            Assert.Empty(page1.Select(a => a.AccountKey).Intersect(page2.Select(a => a.AccountKey)));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE AccountKey IN @Keys", new { Keys = accountKeys });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey IN @Keys", new { Keys = accountKeys });
            await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey IN @Keys", new { Keys = accountKeys });
        }
    }
}
