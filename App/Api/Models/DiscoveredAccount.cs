namespace BlueTrack.Api.Models;

/// <summary>
/// web.discovered_account (AD Account Discovery feature, 2026-09-16): a real
/// AD account not yet onboarded into CyberArk (no dbo.fact_account row),
/// found by matching its AD group membership against web.dim_access_group's
/// already-inventoried groups. ComputedRiskScore comes from
/// usp_CalculateRiskScoreForAccessGroupSet (Database/32) over the matched
/// AccessGroupKeys -- read-only, no override concept here (unlike
/// web.account_risk_score) since this is a candidate, not a tracked account.
/// </summary>
public sealed class DiscoveredAccount
{
    public int DiscoveredAccountKey { get; init; }
    public required string DomainName { get; init; }
    public required string SamAccountName { get; init; }
    public string? DistinguishedName { get; init; }
    public string? ObjectSid { get; init; }
    public string? DisplayName { get; init; }
    public bool IsEnabled { get; init; }
    public int? ComputedRiskScore { get; init; }
    public DateTime? RiskScoreCalculatedDate { get; init; }
    public DateTime DiscoveredDate { get; init; }
    public DateTime LastSeenDate { get; init; }

    /// <summary>Set only when the discovery job's best-effort text match found a plausible-but-not-certain existing account -- surfaced for human review, never used to silently include or exclude a candidate.</summary>
    public long? PossibleExistingAccountKey { get; init; }
    public string? PossibleExistingAccountName { get; init; }

    /// <summary>web.dim_risk_score_band (D-120), same join every other ComputedRiskScore/EffectiveRiskScore display in this app already does.</summary>
    public string? RiskScoreBandName { get; init; }

    /// <summary>'New' | 'Accepted' | 'Dismissed' -- the onboarding-workflow follow-up (2026-09-16). GetListAsync defaults to 'New' only; Accepted/Dismissed rows stay for audit history, not deleted.</summary>
    public required string Status { get; init; }

    /// <summary>Set only on Accept -- the real dbo.fact_account row this candidate became.</summary>
    public long? ResolvedAccountKey { get; init; }
    public int? ReviewedBy { get; init; }
    public DateTime? ReviewedDate { get; init; }
}

/// <summary>
/// What AdAccountDiscoveryService has resolved for one AD account before it's
/// written to web.discovered_account -- the matched AccessGroupKeys drive
/// both the risk score calculation and web.discovered_account_access_group_map.
/// </summary>
public sealed class DiscoveredAccountCandidate
{
    public required string DomainName { get; init; }
    public required string SamAccountName { get; init; }
    public string? DistinguishedName { get; init; }
    public string? ObjectSid { get; init; }
    public string? DisplayName { get; init; }
    public bool IsEnabled { get; init; }
    public required IReadOnlyList<int> MatchedAccessGroupKeys { get; init; }
    public long? PossibleExistingAccountKey { get; init; }
}
