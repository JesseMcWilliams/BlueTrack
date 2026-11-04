namespace BlueTrack.Api.Models;

/// <summary>
/// Blueprint progress KPI rollup: how many accounts are in scope (not
/// excluded by an active Risk Accepted / Excluded exception, and not
/// Archived, D-298), and of those, how many have reached each further stage.
/// Onboarded/Managed/Compliant are each a subset of the tier above it
/// (Compliant &lt;= Managed &lt;= Onboarded &lt;= InScope &lt;= Total), so
/// ratios can be computed directly from these five counts without
/// re-querying. ArchivedAccounts is part of Total - InScope.
/// </summary>
public sealed class KpiSummary
{
    public int TotalAccounts { get; init; }
    public int InScopeAccounts { get; init; }
    public int OnboardedAccounts { get; init; }
    public int ManagedAccounts { get; init; }
    public int CompliantAccounts { get; init; }
    public int ArchivedAccounts { get; init; }

    /// <summary>
    /// D-326: everything discovered: every tracked account (TotalAccounts), plus the AD-discovered candidates that aren't
    /// tracked (not accepted and with no possible tracked match) and the DNA accounts no tracked account matched (each
    /// counted once). The funnel's first step, above All Accounts.
    /// </summary>
    public int DiscoveredAccounts => TotalAccounts + UntrackedAdCandidates + UntrackedDnaAccounts;
    public int UntrackedAdCandidates { get; init; }
    public int UntrackedDnaAccounts { get; init; }
}
