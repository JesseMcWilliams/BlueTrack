namespace BlueTrack.Api.Models;

/// <summary>One row for the new Risk Score report (Design_Risk_Scoring.md, D-101-105, Phase E).</summary>
public sealed class RiskScoreReportRow
{
    public long AccountKey { get; init; }
    public required string AccountName { get; init; }
    public int? ComputedRiskScore { get; init; }
    public int? OverrideRiskScore { get; init; }
    public int? EffectiveRiskScore { get; init; }
    public bool IsRiskScoreStale { get; init; }
    public DateTime? RiskScoreCalculatedDate { get; init; }
}

/// <summary>
/// One contributing Target or Access Group behind an Account's score --
/// the report's drill-down, backed by web.ufn_ReachableRiskValues'
/// EntityType/EntityKey/EntityName columns (24_BlueTrack_RiskScoringReportDrilldown.sql).
/// </summary>
public sealed class RiskScoreContributor
{
    public required string EntityType { get; init; }
    public long EntityKey { get; init; }
    public required string EntityName { get; init; }
    public int RiskValue { get; init; }
}

public sealed class SaveRiskScoreOverrideRequest
{
    public int? OverrideRiskScore { get; init; }
    public string? Reason { get; init; }
}
