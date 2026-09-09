namespace BlueTrack.Api.Models;

/// <summary>
/// web.dim_risk_score_band (D-120): an admin-configurable named band over
/// the computed EffectiveRiskScore (web.account_risk_score) -- deliberately
/// separate from dbo.dim_risk_level (a manually-assigned label with no
/// numeric score, untouched by this feature).
/// </summary>
public sealed class RiskScoreBandSummary
{
    public int RiskScoreBandKey { get; init; }
    public required string BandName { get; init; }
    public required int MinScore { get; init; }
    public required int MaxScore { get; init; }
    public required int RiskOrder { get; init; }
    public DateTime ModifiedDate { get; init; }
}

public sealed class SaveRiskScoreBandRequest
{
    public required string BandName { get; init; }
    public required int MinScore { get; init; }
    public required int MaxScore { get; init; }
    public required int RiskOrder { get; init; }
}
