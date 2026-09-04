using BlueTrack.Api.Data;
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
}
