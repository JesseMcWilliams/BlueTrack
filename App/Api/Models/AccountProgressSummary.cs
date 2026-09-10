namespace BlueTrack.Api.Models;

/// <summary>
/// One row for the Account Progress list/grid page (Design_Application_Structure.md).
/// A read projection, not the full fact_account_progress shape.
/// </summary>
public sealed class AccountProgressSummary
{
    public long AccountKey { get; init; }
    public required string AccountName { get; init; }

    // D-133: the list page shows these instead of AccountName -- fact_account's
    // own UserName/Address, distinct from the synthetic AccountName CyberArk
    // builds for its own UI (typically some combination of the two plus the
    // Safe/Platform). AccountName is kept on this model regardless (still the
    // account detail page's page-title fallback), just no longer the list's
    // display column.
    public string? UserName { get; init; }
    public string? Address { get; init; }

    public required string StageName { get; init; }
    public required string StatusName { get; init; }
    public string? RiskLevelName { get; init; }
    public string? OwnerName { get; init; }
    public DateTime? TargetRemediationDate { get; init; }
    public DateTime? ActualCompletionDate { get; init; }

    /// <summary>web.account_risk_score.EffectiveRiskScore (D-101-105, Phase E) -- null until this Account has ever been scored.</summary>
    public int? EffectiveRiskScore { get; init; }

    /// <summary>web.dim_risk_score_band (D-120) -- null when EffectiveRiskScore is null, or falls outside every configured band's range.</summary>
    public string? RiskScoreBandName { get; init; }
}
