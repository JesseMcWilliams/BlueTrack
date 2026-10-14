namespace BlueTrack.Api.Models;

/// <summary>
/// D-221: the Reports &gt; Trends page's three series, from the daily
/// snapshot table dbo.fact_account_progress_history. One period per snapshot
/// day (Day) or per ISO week, Monday to Sunday (Week, using the week's last
/// snapshot in the range, not a sum). StageCounts lines up with Stages and
/// RiskCounts with RiskLevels (whose last entry is "Not set", for snapshots
/// with no risk level).
/// </summary>
public sealed class TrendsReport
{
    public required DateOnly From { get; init; }
    public required DateOnly To { get; init; }
    public required string Granularity { get; init; }
    public int? ApplicationKey { get; init; }
    public int? SafeKey { get; init; }
    public required IReadOnlyList<TrendsSeries> Stages { get; init; }
    public required IReadOnlyList<TrendsSeries> RiskLevels { get; init; }
    public required IReadOnlyList<TrendsPeriod> Periods { get; init; }
}

/// <summary>One series: its order (StageOrder / RiskOrder; null for "Not set") and display name.</summary>
public sealed record TrendsSeries(int? Order, string Name);

public sealed class TrendsPeriod
{
    /// <summary>The day (Day), or the Monday the ISO week starts (Week).</summary>
    public required DateOnly PeriodStart { get; init; }
    /// <summary>The snapshot the period's values come from.</summary>
    public required DateOnly SnapshotDate { get; init; }
    public required int TotalAccounts { get; init; }
    /// <summary>D-143's rule: not status Risk Accepted / Excluded.</summary>
    public required int InScopeAccounts { get; init; }
    /// <summary>D-143's rule: the final stage (StageOrder 5) and status Complete.</summary>
    public required int CompliantAccounts { get; init; }
    /// <summary>Compliant / In Scope x 100, one decimal; null with nothing in scope.</summary>
    public double? PercentComplete { get; init; }
    public required IReadOnlyList<int> StageCounts { get; init; }
    public required IReadOnlyList<int> RiskCounts { get; init; }
}

/// <summary>D-221: the Trends page's Application and Safe filter lists (ViewDashboard; the Safes list elsewhere needs CurateApplicationMapping).</summary>
public sealed class TrendsFilterOptions
{
    public required IReadOnlyList<ApplicationSummary> Applications { get; init; }
    public required IReadOnlyList<TrendsSafeOption> Safes { get; init; }
}

public sealed class TrendsSafeOption
{
    public int SafeKey { get; init; }
    public string SafeName { get; init; } = "";
    public string SourceSystemName { get; init; } = "";
    public int? ApplicationKey { get; init; }
}
