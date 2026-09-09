namespace BlueTrack.Api.Models;

/// <summary>
/// web.dim_access_group (Design_Risk_Scoring.md, D-101-105) -- a
/// privileged-access group in the MANAGED environment (e.g. an AD "Server
/// Admins" group) -- distinct from dbo.dim_group (CyberArk vault-internal
/// Safe permissions) and web.identity_group_role_map (BlueTrack's own
/// login/authorization group mapping).
/// </summary>
public sealed class AccessGroupSummary
{
    public int AccessGroupKey { get; init; }
    public required string GroupName { get; init; }
    public required string GroupIdentifier { get; init; }
    public required string GroupScope { get; init; }
    public int? FoundOnTargetKey { get; init; }
    public string? FoundOnTargetName { get; init; }
    public string? DiscoverySource { get; init; }
    // D-121: additive alongside GroupScope/FoundOnTargetKey (D-119), which
    // stay exactly as they are -- SorTypeKey/SorAddress name where the
    // group's Source of Record actually lives, a separate concept entirely.
    public int? SorTypeKey { get; init; }
    public string? SorTypeName { get; init; }
    public string? SorAddress { get; init; }
    public required int BaseRiskScore { get; init; }
    public int? ComputedRiskScore { get; init; }
    public bool IsRiskScoreStale { get; init; }
    public string? Description { get; init; }
    public DateTime ModifiedDate { get; init; }
}

public sealed class SaveAccessGroupRequest
{
    public required string GroupName { get; init; }
    public required string GroupIdentifier { get; init; }
    public required string GroupScope { get; init; }
    public int? FoundOnTargetKey { get; init; }
    public string? DiscoverySource { get; init; }
    public int? SorTypeKey { get; init; }
    public string? SorAddress { get; init; }
    public required int BaseRiskScore { get; init; }
    public string? Description { get; init; }
}

/// <summary>D-121: web.dim_sor_type -- the controlled list of where an Access Group's Source of Record lives (Domain/Local/App, extensible).</summary>
public sealed class SorTypeSummary
{
    public int SorTypeKey { get; init; }
    public required string SorTypeName { get; init; }
}
