using System.Globalization;
using Dapper;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// D-222: the weekly summary, on the day and at the time set on the
/// Notifications page (server local time), once per ISO week (DedupKey).
/// The KPI Summary's numbers (D-143), the change since the snapshot a week
/// earlier (dbo.fact_account_progress_history, D-221's Trends data; "no
/// history" when there's no snapshot that old), the Overdue / At-Risk
/// account count, the Risk Exceptions overdue for review, and the last
/// Import and Load runs.
/// </summary>
public sealed class WeeklySummaryCheck(
    NotificationRepository notificationRepository,
    ReportsRepository reportsRepository,
    TrendsRepository trendsRepository,
    RiskExceptionRepository riskExceptionRepository,
    IDbConnectionFactory connectionFactory,
    TimeProvider timeProvider) : INotificationCheck
{
    public const string TypeName = "WeeklySummary";

    public string NotificationTypeName => TypeName;
    public TimeSpan Cooldown => TimeSpan.Zero;   // Unused: the result has a DedupKey.

    public async Task<IReadOnlyList<NotificationCheckResult>> EvaluateAsync()
    {
        var schedule = await notificationRepository.GetScheduleAsync();
        var localNow = timeProvider.GetLocalNow().DateTime;
        var dedupKey = DigestSchedule.WeeklyDueKey(localNow, schedule.WeeklySummaryDay, schedule.WeeklySummaryTime);
        if (dedupKey is null || await notificationRepository.WasSentAsync(TypeName, dedupKey))
        {
            return [];
        }

        var kpi = await reportsRepository.GetKpiSummaryAsync();
        var today = DateOnly.FromDateTime(localNow);
        var trends = await trendsRepository.GetTrendsAsync(today.AddDays(-14), today, TrendsRepository.Day, null, null);
        var overdueAccounts = (await reportsRepository.GetOverdueAtRiskListAsync()).Count;
        var overdueExceptions = (await riskExceptionRepository.GetOverdueReviewAsync()).Count;
        var runs = await GetLastRunsAsync();

        var email = new NotificationEmail($"BlueTrack weekly summary, {localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");

        email.Heading("Key progress indicators (KPI Summary)").Facts(
        [
            ("In Scope vs. All Accounts", NotificationEmail.Ratio(kpi.InScopeAccounts, kpi.TotalAccounts)),
            ("Onboarded vs. In Scope", NotificationEmail.Ratio(kpi.OnboardedAccounts, kpi.InScopeAccounts)),
            ("Managed vs. Onboarded", NotificationEmail.Ratio(kpi.ManagedAccounts, kpi.OnboardedAccounts)),
            ("Compliant vs. Managed", NotificationEmail.Ratio(kpi.CompliantAccounts, kpi.ManagedAccounts)),
        ]);

        email.Heading("Change since last week (Trends)");
        AppendChange(email, trends);

        email.Heading("Needs attention").Facts(
        [
            ("Overdue / At-Risk accounts", NotificationEmail.Count(overdueAccounts)),
            ("Risk Exceptions overdue for review", NotificationEmail.Count(overdueExceptions)),
        ]);

        email.Heading("Last Import and Load").Facts(runs);
        email.SeePage("Reports > KPI Summary, Reports > Trends and Reports > Overdue / At-Risk");

        return
        [
            new NotificationCheckResult(
                Subject: "BlueTrack: weekly summary",
                Body: email.Text,
                Detail: $"Weekly summary ({dedupKey})",
                HtmlBody: email.Html,
                DedupKey: dedupKey)
        ];
    }

    /// <summary>The latest snapshot against the latest one at least 7 days older.</summary>
    private static void AppendChange(NotificationEmail email, TrendsReport trends)
    {
        var latest = trends.Periods.LastOrDefault();
        var previous = latest is null ? null : trends.Periods.LastOrDefault(p => p.SnapshotDate <= latest.SnapshotDate.AddDays(-7));
        if (latest is null || previous is null)
        {
            email.Paragraph("No history yet: there's no daily snapshot from a week ago to compare with. The nightly Load takes one each night.");
            return;
        }

        static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        static string Pct(double? value) => value is null ? "N/A" : value.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%";
        static string Delta(int change) => change == 0 ? "no change" : (change > 0 ? "+" : "") + change.ToString("N0", CultureInfo.InvariantCulture);
        string PctDelta() => latest.PercentComplete is null || previous.PercentComplete is null
            ? "" : $" ({(latest.PercentComplete >= previous.PercentComplete ? "+" : "")}{(latest.PercentComplete.Value - previous.PercentComplete.Value).ToString("0.0", CultureInfo.InvariantCulture)} points)";

        email.Paragraph($"Snapshot of {Date(latest.SnapshotDate)} compared with {Date(previous.SnapshotDate)}.");
        email.Facts([("% complete", $"{Pct(previous.PercentComplete)} to {Pct(latest.PercentComplete)}{PctDelta()}")]);
        email.Table(["Stage", Date(previous.SnapshotDate), Date(latest.SnapshotDate), "Change"],
            trends.Stages.Select((stage, i) => (IReadOnlyList<string>)
            [
                stage.Name,
                NotificationEmail.Count(previous.StageCounts[i]),
                NotificationEmail.Count(latest.StageCounts[i]),
                Delta(latest.StageCounts[i] - previous.StageCounts[i])
            ]));
    }

    private sealed class RunRow
    {
        public required string StepName { get; init; }
        public DateTime StartedAt { get; init; }
        public required string Status { get; init; }
        public string? ErrorMessage { get; init; }
    }

    private async Task<IReadOnlyList<(string, string)>> GetLastRunsAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT StepName, StartedAt, Status, ErrorMessage
            FROM (SELECT StepName, StartedAt, Status, ErrorMessage,
                         ROW_NUMBER() OVER (PARTITION BY StepName ORDER BY EtlRunKey DESC) AS rn
                  FROM dbo.etl_run_log) r
            WHERE rn = 1;

            SELECT TOP 1 StartedAt FROM dbo.import_log ORDER BY StartedAt DESC;
            """;
        using var grid = await connection.QueryMultipleAsync(sql);
        var runs = (await grid.ReadAsync<RunRow>()).ToDictionary(r => r.StepName);
        var lastFile = await grid.ReadSingleOrDefaultAsync<DateTime?>();

        var facts = new List<(string, string)>();
        foreach (var step in new[] { "Import", "Load" })
        {
            facts.Add((step, runs.TryGetValue(step, out var run)
                ? $"{run.Status}, started {NotificationEmail.ServerTime(run.StartedAt, timeProvider)} (server time)"
                  + (run.Status == "Failed" && run.ErrorMessage is not null ? $": {run.ErrorMessage}" : "")
                : "no run recorded yet"));
        }
        if (runs.Count == 0)
        {
            facts.Add(("Last file imported", lastFile is null ? "never" : NotificationEmail.ServerTime(lastFile.Value, timeProvider) + " (server time)"));
        }
        return facts;
    }
}
