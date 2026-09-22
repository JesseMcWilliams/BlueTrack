namespace BlueTrack.Api.Models;

/// <summary>
/// Blueprint progress KPI rollup: how many accounts are in scope (not
/// excluded by an active Risk Accepted / Excluded exception), and of those,
/// how many have reached each further stage. Onboarded/Managed/Compliant are
/// each a subset of the tier above it (Compliant &lt;= Managed &lt;=
/// Onboarded &lt;= InScope &lt;= Total), so ratios can be computed directly
/// from these five counts without re-querying.
/// </summary>
public sealed class KpiSummary
{
    public int TotalAccounts { get; init; }
    public int InScopeAccounts { get; init; }
    public int OnboardedAccounts { get; init; }
    public int ManagedAccounts { get; init; }
    public int CompliantAccounts { get; init; }
}
