using BlueTrack.Api.Models;

namespace BlueTrack.Api.Reports;

/// <summary>D-220: a report as an export knows it -- its key (the audit EntityKey), file-name stem and title (the sheet name and audit text).</summary>
public sealed record ReportExportInfo(string Key, string FileStem, string Title);

/// <summary>
/// D-220: every exportable report and its columns, defined explicitly (no
/// reflection over the DTOs): the same headers, in the same order, as the
/// report's grid on its page (App/Web/src/views/reports). Where the grid
/// combines several fields in one cell, so does the export, in the same
/// words. Action columns (Link, Accept, ...) aren't exported. Change a grid
/// and its columns here together.
/// </summary>
public static class ReportExports
{
    public static readonly ReportExportInfo OverdueAtRisk = new("overdue-at-risk", "OverdueAtRisk", "Overdue / At-Risk");
    public static readonly ReportExportInfo StageStatusSummary = new("stage-status-summary", "StageStatusSummary", "Stage/Status Summary");
    public static readonly ReportExportInfo ReconciliationReview = new("reconciliation-review-queue", "ReconciliationReview", "Reconciliation Review");
    public static readonly ReportExportInfo PossibleMatches = new("possible-matches", "PossibleAccountMatches", "Possible Account Matches");
    public static readonly ReportExportInfo UnresolvedEntitlementMembers = new("unresolved-entitlement-members", "UnresolvedEntitlementMembers", "Unresolved Entitlement Members");
    public static readonly ReportExportInfo RiskScore = new("risk-score", "RiskScore", "Risk Score");
    public static readonly ReportExportInfo RiskExceptionSod = new("risk-exception-sod", "RiskExceptionSoD", "Risk Exception SoD");
    public static readonly ReportExportInfo DiscoveredAccounts = new("discovered-accounts", "DiscoveredAccounts", "Discovered Accounts");
    public static readonly ReportExportInfo DecomSafes = new("decom-safes", "SafesFlaggedForDeletion", "Safes Flagged for Deletion");
    public static readonly ReportExportInfo DecomAccounts = new("decom-accounts", "AccountsFlaggedForDeletion", "Accounts Flagged for Deletion");
    public static readonly ReportExportInfo KpiSummary = new("kpi-summary", "KpiSummary", "KPI Summary");
    public static readonly ReportExportInfo Trends = new("trends", "Trends", "Trends");

    // The pages show every date as yyyy-MM-dd (formatDate), so the exports do too.
    private static DateOnly? Day(DateTime? value) => value is { } v ? DateOnly.FromDateTime(v) : null;
    private static string YesNo(bool value) => value ? "Yes" : "No";

    public static readonly IReadOnlyList<ExportColumn<AccountProgressSummary>> OverdueAtRiskColumns =
    [
        new("Account", r => r.AccountName),
        new("Stage", r => r.StageName),
        new("Status", r => r.StatusName),
        new("Risk Level", r => r.RiskLevelName),
        new("Owner", r => r.OwnerName),
        new("Target Remediation Date", r => Day(r.TargetRemediationDate))
    ];

    public static readonly IReadOnlyList<ExportColumn<ReconciliationReviewItem>> ReconciliationReviewColumns =
    [
        new("Self-Hosted Account", r => $"{r.SelfHostedAccountName} ({r.SelfHostedUserName}@{r.SelfHostedAddress})"),
        new("Privilege Cloud Account", r => $"{r.PrivCloudAccountName} ({r.PrivCloudUserName}@{r.PrivCloudAddress})"),
        new("Match Method", r => r.MatchMethod),
        new("Confidence", r => r.MatchConfidence),
        new("Matched Date", r => Day(r.MatchedDate)),
        new("Notes", r => r.Notes)
    ];

    // PossibleAccountMatches.vue's methodLabels.
    private static readonly IReadOnlyDictionary<string, string> MatchMethodLabels = new Dictionary<string, string>
    {
        ["FeedShortName"] = "Same host name",
        ["FeedDnsForward"] = "Same IP address (DNS)",
        ["FeedDnsReverse"] = "Reverse DNS"
    };

    public static readonly IReadOnlyList<ExportColumn<PossibleMatchRow>> PossibleMatchesColumns =
    [
        new("Other system's account", r => $"{r.FeedUserName} on {r.FeedAddress} ({r.FeedSourceName})"),
        new("CyberArk account", r => $"{r.CyberArkAccountName} ({r.CyberArkUserName} on {r.CyberArkAddress}, {r.CyberArkSourceName})"),
        new("Why", r =>
        {
            var label = MatchMethodLabels.TryGetValue(r.MatchMethod, out var l) ? l : r.MatchMethod;
            return string.IsNullOrWhiteSpace(r.Reason) ? label : $"{label}: {r.Reason}";
        }),
        new("Found", r => Day(r.MatchedDate))
    ];

    public static readonly IReadOnlyList<ExportColumn<UnresolvedEntitlementMember>> UnresolvedEntitlementMembersColumns =
    [
        new("Safe", r => r.SafeName),
        new("Member Type", r => r.MemberType),
        new("Unresolved Identifier", r => r.UnresolvedMemberId),
        new("Membership Expiration", r => Day(r.MembershipExpirationDate)),
        new("Snapshot Date", r => Day(r.SnapshotDate))
    ];

    public static readonly IReadOnlyList<ExportColumn<RiskScoreReportRow>> RiskScoreColumns =
    [
        new("Account", r => r.AccountName),
        new("Computed", r => r.ComputedRiskScore),
        new("Override", r => r.OverrideRiskScore),
        new("Effective", r => r.EffectiveRiskScore),
        new("Risk Band", r => r.RiskScoreBandName),
        new("Stale?", r => YesNo(r.IsRiskScoreStale)),
        new("Last Calculated", r => Day(r.RiskScoreCalculatedDate))
    ];

    public static readonly IReadOnlyList<ExportColumn<RiskExceptionSodViolation>> RiskExceptionSodColumns =
    [
        new("Exception ID", r => r.ExceptionID),
        new("Account", r => r.AccountName),
        new("User", r => r.UserDisplayName),
        new("Linked On", r => Day(r.OccurredAt))
    ];

    public static readonly IReadOnlyList<ExportColumn<DiscoveredAccount>> DiscoveredAccountsColumns =
    [
        new("Account", r => r.SamAccountName
            + (string.IsNullOrEmpty(r.DisplayName) ? "" : $" ({r.DisplayName})")
            + (r.IsEnabled ? "" : " [disabled]")),
        new("Domain", r => r.DomainName),
        new("Risk Score", r => r.ComputedRiskScore),
        new("Risk Band", r => r.RiskScoreBandName),
        new("First Discovered", r => Day(r.DiscoveredDate)),
        new("Last Seen", r => Day(r.LastSeenDate))
    ];

    public static readonly IReadOnlyList<ExportColumn<DecomSafeRow>> DecomSafesColumns =
    [
        new("Safe", r => r.SafeName),
        new("Source", r => r.SourceSystemName),
        new("Application", r => r.ApplicationName),
        new("Accounts not yet deleted", r => r.ActiveAccountCount),
        new("Deleted accounts", r => r.DeletedAccountCount)
    ];

    public static readonly IReadOnlyList<ExportColumn<DecomAccountRow>> DecomAccountsColumns =
    [
        new("Username", r => string.IsNullOrEmpty(r.UserName) ? $"{r.AccountName} (no username)" : r.UserName),
        new("Address", r => r.Address),
        new("Account Name", r => r.AccountName),
        new("Safe", r => r.SafeName),
        new("Source", r => r.SourceSystemName),
        new("Flagged by", r => string.Join(", ", new[] { r.InFlaggedSafe ? "Safe flagged" : null, r.NameFlagged ? "Name flagged" : null }.OfType<string>())),
        new("Note", r => (r.IsDeleted ? "Deleted" : "Not deleted yet.") + (string.IsNullOrEmpty(r.OtherSafes) ? "" : $" Also in: {r.OtherSafes}"))
    ];

    /// <summary>
    /// The Stage/Status grid is a pivot: one row per stage (in stage order), a
    /// column per status (in the order the rows list them), then Total --
    /// StageStatusFunnelSummary.vue's statuses/stages/countFor/stageTotal.
    /// </summary>
    public static ReportTable StageStatusTable(IReadOnlyList<StageStatusFunnelRow> rows)
    {
        var statuses = rows.Select(r => r.StatusName).Distinct().ToList();
        var stages = rows.GroupBy(r => r.StageOrder).OrderBy(g => g.Key).Select(g => g.First().StageName).ToList();
        var headers = new List<string> { "Stage" };
        headers.AddRange(statuses);
        headers.Add("Total");

        var table = stages.Select(stage =>
        {
            var cells = new List<object?> { stage };
            cells.AddRange(statuses.Select(status =>
                (object?)(rows.FirstOrDefault(r => r.StageName == stage && r.StatusName == status)?.AccountCount ?? 0)));
            cells.Add(rows.Where(r => r.StageName == stage).Sum(r => r.AccountCount));
            return cells.ToArray();
        }).ToList();
        return new ReportTable(headers, table);
    }

    /// <summary>KpiSummary.vue's four ratios: KPI, Count ("n / d", as shown), Percentage (one decimal; "N/A" with no denominator).</summary>
    public static ReportTable KpiTable(KpiSummary s)
    {
        (string Label, int Numerator, int Denominator)[] kpis =
        [
            ("Accounts In Scope vs. All Accounts", s.InScopeAccounts, s.TotalAccounts),
            ("Accounts Onboarded vs. Accounts In Scope", s.OnboardedAccounts, s.InScopeAccounts),
            ("Accounts Managed vs. Onboarded", s.ManagedAccounts, s.OnboardedAccounts),
            ("Accounts Managed vs. Compliance (% of Managed that are Compliant)", s.CompliantAccounts, s.ManagedAccounts)
        ];
        return new ReportTable(["KPI", "Count", "Percentage"], kpis.Select(k => new object?[]
        {
            k.Label,
            $"{k.Numerator} / {k.Denominator}",
            k.Denominator == 0 ? "N/A" : Math.Round((double)k.Numerator / k.Denominator * 100, 1, MidpointRounding.AwayFromZero)
        }).ToList());
    }

    /// <summary>
    /// D-221: one row per period, all three charts' series side by side: Period and
    /// Snapshot Date, the accounts at each stage, Total / In Scope / Compliant /
    /// % Complete (blank with nothing in scope), then the accounts at each risk level
    /// and "Not set". The same table as the page's "Show data tables", in one sheet.
    /// </summary>
    public static ReportTable TrendsTable(TrendsReport report)
    {
        var headers = new List<string> { "Period", "Snapshot Date" };
        headers.AddRange(report.Stages.Select(s => $"Stage: {s.Name}"));
        headers.AddRange(["Total Accounts", "In Scope", "Compliant", "% Complete"]);
        headers.AddRange(report.RiskLevels.Select(r => $"Risk: {r.Name}"));

        var rows = report.Periods.Select(p =>
        {
            var cells = new List<object?> { p.PeriodStart, p.SnapshotDate };
            cells.AddRange(p.StageCounts.Select(c => (object?)c));
            cells.AddRange([p.TotalAccounts, p.InScopeAccounts, p.CompliantAccounts, p.PercentComplete]);
            cells.AddRange(p.RiskCounts.Select(c => (object?)c));
            return cells.ToArray();
        }).ToList();
        return new ReportTable(headers, rows);
    }
}
