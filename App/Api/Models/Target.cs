namespace BlueTrack.Api.Models;

/// <summary>web.dim_target (Design_Risk_Scoring.md, D-101-105) -- any final destination an account's access leads to.</summary>
public sealed class TargetSummary
{
    public int TargetKey { get; init; }
    public required string TargetType { get; init; }
    public required string TargetName { get; init; }
    public Guid InternalGuid { get; init; }
    public int? ApplicationKey { get; init; }
    public string? ApplicationName { get; init; }
    public required int RiskScore { get; init; }
    public string? Description { get; init; }
    public string? DiscoverySource { get; init; }
    public DateTime ModifiedDate { get; init; }
    public required IReadOnlyList<TargetIdentifier> Identifiers { get; init; }
}

public sealed class TargetIdentifier
{
    public required string IdentifierType { get; init; }
    public required string IdentifierValue { get; init; }
}

public sealed class SaveTargetRequest
{
    public required string TargetType { get; init; }
    public required string TargetName { get; init; }
    public int? ApplicationKey { get; init; }
    public required int RiskScore { get; init; }
    public string? Description { get; init; }
    public string? DiscoverySource { get; init; }
    public required IReadOnlyList<SaveTargetIdentifierRequest> Identifiers { get; init; }
}

public sealed class SaveTargetIdentifierRequest
{
    public required string IdentifierType { get; init; }
    public required string IdentifierValue { get; init; }
}

public sealed class TargetIdentifierTypeSummary
{
    public required string IdentifierType { get; init; }
    public int MatchPriority { get; init; }
    public bool RequiresReview { get; init; }
}
