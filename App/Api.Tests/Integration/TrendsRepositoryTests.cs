using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>D-221: Reports > Trends against real BlueTrackTest, on the snapshots TrendsTestData seeds and removes.</summary>
public class TrendsRepositoryTests : IAsyncLifetime
{
    private TrendsTestData.Keys _keys = null!;
    private readonly TrendsRepository _repository = new(new TestDbConnectionFactory());

    public async Task InitializeAsync() => _keys = await TrendsTestData.SeedAsync();

    public Task DisposeAsync() => TrendsTestData.RemoveAsync();

    private Task<TrendsReport> Get(string granularity = TrendsRepository.Day, int? applicationKey = null, int? safeKey = null) =>
        _repository.GetTrendsAsync(TrendsTestData.From, TrendsTestData.To, granularity, applicationKey, safeKey);

    private static TrendsPeriod On(TrendsReport report, int day) =>
        Assert.Single(report.Periods, p => p.SnapshotDate == new DateOnly(2026, 2, day));

    [Fact]
    public async Task Day_HasOnePeriodPerSnapshot_WithEveryStageAndRiskLevel()
    {
        var report = await Get();

        Assert.Equal([new DateOnly(2026, 2, 2), new DateOnly(2026, 2, 4), new DateOnly(2026, 2, 10)], report.Periods.Select(p => p.PeriodStart));
        Assert.Equal([1, 2, 3, 4, 5], report.Stages.Select(s => s.Order!.Value));
        Assert.Equal(["Low", "Medium", "High", "Critical", "Not set"], report.RiskLevels.Select(r => r.Name));
        Assert.Null(report.RiskLevels[^1].Order);

        var feb4 = On(report, 4);
        Assert.Equal([1, 0, 1, 0, 1], feb4.StageCounts);
        Assert.Equal([1, 1, 1, 0, 0], feb4.RiskCounts);
        Assert.Equal(3, feb4.TotalAccounts);

        var feb2 = On(report, 2);
        Assert.Equal([2, 1, 0, 0, 0], feb2.StageCounts);
        Assert.Equal([1, 0, 1, 0, 1], feb2.RiskCounts);   // A1 has no risk level: "Not set"
    }

    [Fact]
    public async Task PercentComplete_UsesTheKpiRule_FinalStageAndComplete_OverNotRiskAccepted()
    {
        var report = await Get();

        // Feb 4: B1 is Risk Accepted / Excluded (out of scope); A2 is stage 5 Complete; A1 isn't.
        var feb4 = On(report, 4);
        Assert.Equal(2, feb4.InScopeAccounts);
        Assert.Equal(1, feb4.CompliantAccounts);
        Assert.Equal(50.0, feb4.PercentComplete);

        // Feb 10: B1 is at stage 5 but In Progress -- not compliant.
        var feb10 = On(report, 10);
        Assert.Equal(3, feb10.InScopeAccounts);
        Assert.Equal(2, feb10.CompliantAccounts);
        Assert.Equal(66.7, feb10.PercentComplete);
    }

    [Fact]
    public async Task Week_UsesTheLastSnapshotInEachIsoWeek_NotASum()
    {
        var report = await Get(TrendsRepository.Week);

        Assert.Equal(TrendsRepository.Week, report.Granularity);
        Assert.Equal(2, report.Periods.Count);
        var first = report.Periods[0];
        Assert.Equal(new DateOnly(2026, 2, 2), first.PeriodStart);    // Monday
        Assert.Equal(new DateOnly(2026, 2, 4), first.SnapshotDate);   // Wednesday, the week's last snapshot
        Assert.Equal(3, first.TotalAccounts);
        Assert.Equal([1, 0, 1, 0, 1], first.StageCounts);
        var second = report.Periods[1];
        Assert.Equal(new DateOnly(2026, 2, 9), second.PeriodStart);
        Assert.Equal(new DateOnly(2026, 2, 10), second.SnapshotDate);
    }

    [Fact]
    public async Task Week_TakesTheLastSnapshotInsideTheRange()
    {
        var report = await _repository.GetTrendsAsync(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 3), TrendsRepository.Week, null, null);

        var only = Assert.Single(report.Periods);
        Assert.Equal(new DateOnly(2026, 2, 2), only.SnapshotDate);
    }

    [Fact]
    public async Task ApplicationFilter_CountsOnlyAccountsInItsSafes()
    {
        var report = await Get(applicationKey: _keys.ApplicationKey);

        var feb2 = On(report, 2);
        Assert.Equal(2, feb2.TotalAccounts);
        Assert.Equal([1, 1, 0, 0, 0], feb2.StageCounts);
        Assert.Equal([1, 0, 0, 0, 1], feb2.RiskCounts);
        Assert.Equal(100.0, On(report, 10).PercentComplete);
    }

    [Fact]
    public async Task SafeFilter_CountsOnlyAccountsInThatSafe()
    {
        var report = await Get(safeKey: _keys.SafeBKey);

        var feb4 = On(report, 4);
        Assert.Equal(1, feb4.TotalAccounts);
        Assert.Equal(0, feb4.InScopeAccounts);
        Assert.Null(feb4.PercentComplete);   // nothing in scope: no percentage, not 0%
        var feb10 = On(report, 10);
        Assert.Equal([0, 0, 0, 0, 1], feb10.StageCounts);
        Assert.Equal([0, 0, 0, 1, 0], feb10.RiskCounts);
        Assert.Equal(0.0, feb10.PercentComplete);
    }

    [Fact]
    public async Task ApplicationAndSafeThatDontMatch_GiveZeroCounts_OnTheSameSnapshotDays()
    {
        var report = await Get(applicationKey: _keys.ApplicationKey, safeKey: _keys.SafeBKey);

        Assert.Equal(3, report.Periods.Count);
        Assert.All(report.Periods, p => Assert.Equal(0, p.TotalAccounts));
    }

    [Fact]
    public async Task ARangeWithNoSnapshots_HasNoPeriods()
    {
        var report = await _repository.GetTrendsAsync(new DateOnly(2026, 1, 5), new DateOnly(2026, 1, 10), TrendsRepository.Day, null, null);

        Assert.Empty(report.Periods);
        Assert.Equal(5, report.Stages.Count);
    }

    [Fact]
    public async Task FilterOptions_ListTheApplication_AndSafesWithAccounts_WithTheirApplication()
    {
        var options = await _repository.GetFilterOptionsAsync();

        Assert.Contains(options.Applications, a => a.ApplicationKey == _keys.ApplicationKey);
        var safeA = Assert.Single(options.Safes, s => s.SafeKey == _keys.SafeAKey);
        Assert.Equal(_keys.ApplicationKey, safeA.ApplicationKey);
        Assert.Equal("TrendsTestSafeA", safeA.SafeName);
        Assert.Null(Assert.Single(options.Safes, s => s.SafeKey == _keys.SafeBKey).ApplicationKey);
    }
}

/// <summary>
/// D-221: a snapshot taken today (usp_Load_FactAccountProgressHistory) gives
/// the KPI Summary's own Compliant and In Scope counts (D-143). The one
/// difference is by design: the KPI counts an account with no Account
/// Progress row as in scope, and the snapshot can't hold one (it has no
/// stage), so those are subtracted here.
/// </summary>
public class TrendsMatchesKpiSummaryTests : IAsyncLifetime
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Now);
    private int TodayKey => int.Parse(Today.ToString("yyyyMMdd"));

    public async Task InitializeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("EXEC dbo.usp_Load_FactAccountProgressHistory");
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE SnapshotDateKey = @TodayKey", new { TodayKey });
    }

    [Fact]
    public async Task TodaysSnapshot_GivesTheKpiSummarysCompliantAndInScope()
    {
        var kpi = await new ReportsRepository(new TestDbConnectionFactory()).GetKpiSummaryAsync();
        var report = await new TrendsRepository(new TestDbConnectionFactory()).GetTrendsAsync(Today, Today, TrendsRepository.Day, null, null);

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var withoutProgress = await connection.QuerySingleAsync<int>("""
            SELECT COUNT(*) FROM dbo.fact_account fa
            WHERE fa.IsDeleted = 0
              AND NOT EXISTS (SELECT 1 FROM dbo.vw_linked_feed_account l WHERE l.AccountKey = fa.AccountKey)
              AND NOT EXISTS (SELECT 1 FROM dbo.fact_account_progress fap WHERE fap.AccountKey = fa.AccountKey)
            """);

        var today = Assert.Single(report.Periods);
        Assert.Equal(kpi.CompliantAccounts, today.CompliantAccounts);
        Assert.Equal(kpi.InScopeAccounts - withoutProgress, today.InScopeAccounts);
        Assert.Equal(kpi.TotalAccounts - withoutProgress, today.TotalAccounts);
        Assert.Equal(TrendsRepository.PercentComplete(kpi.CompliantAccounts, kpi.InScopeAccounts - withoutProgress), today.PercentComplete);
    }
}
