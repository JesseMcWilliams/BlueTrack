namespace BlueTrack.Api.Models;

/// <summary>
/// One row for the Account Progress list/grid page (Design_Application-Structure.md).
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

    // D-185: deleted in CyberArk (IsDeletedInSource) or in BlueTrack (who,
    // when, why -- web.account_deletion). IsDeleted is either.
    public bool IsDeleted { get; init; }
    public bool IsDeletedInSource { get; init; }
    public string? DeletedByName { get; init; }
    public DateTime? DeletedAt { get; init; }
    public string? DeletionReason { get; init; }

    // D-186: flagged for deletion -- in a safe matching the safe pattern
    // (DecomSafeName), or its own name matches the account pattern.
    // DecomOtherSafes: other safes holding the same account (username + address).
    public bool? DecomInFlaggedSafe { get; init; }
    public bool? DecomNameFlagged { get; init; }
    public string? DecomSafeName { get; init; }
    public string? DecomOtherSafes { get; init; }

    /// <summary>D-186: its safe matches the ignored-safe pattern -- shown as "Not imported (ignored safe)" rather than "Deleted in CyberArk".</summary>
    public bool IsInIgnoredSafe { get; init; }
}
