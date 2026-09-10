namespace BlueTrack.Api.Models;

/// <summary>
/// The full editable fact_account_progress row shape for the Account
/// Progress edit form. AccountName/ExceptionKey are read-only context, not
/// directly editable here -- ExceptionKey is set by the Risk Exception
/// workflow, not hand-typed (see Design_Risk_Exception_Tracking.md).
/// </summary>
public sealed class AccountProgressDetail
{
    public long ProgressKey { get; init; }
    public long AccountKey { get; init; }
    public required string AccountName { get; init; }
    public int CurrentStageKey { get; init; }
    public int CurrentStatusKey { get; init; }
    public int? RiskLevelKey { get; init; }
    public int? AccountTypeKey { get; init; }
    public int? SORKey { get; init; }
    public string? OwnerName { get; init; }
    public string? BusinessUnit { get; init; }
    public DateTime? TargetRemediationDate { get; init; }
    public DateTime? ActualCompletionDate { get; init; }
    public string? Notes { get; init; }
    public DateTime LastUpdated { get; init; }
    public int? ExceptionKey { get; init; }

    // D-127: moved here from the Account Progress list's own inline "Edit
    // Override" (removed) -- ComputedRiskScore/RiskScoreBandName are always
    // read-only (system-calculated); OverrideRiskScore is the one editable
    // value here, via the existing PUT .../risk-score-override endpoint
    // (unchanged). EffectiveRiskScore = COALESCE(OverrideRiskScore,
    // ComputedRiskScore), same as everywhere else this triad appears.
    public int? ComputedRiskScore { get; init; }
    public int? OverrideRiskScore { get; init; }
    public int? EffectiveRiskScore { get; init; }
    public string? RiskScoreBandName { get; init; }
}

/// <summary>
/// Body for PUT /api/account-progress/{accountKey}. Reason is required only
/// when regressing CurrentStageKey to a lower StageOrder (D-51) -- the API
/// enforces this, the field isn't blanket-required. ExceptionKey is
/// required only when CurrentStatusKey resolves to "Risk Accepted /
/// Excluded" (Design_Risk_Exception_Tracking.md workflow step 2) -- ignored
/// (and the stored value cleared) for every other status.
/// </summary>
public sealed class SaveAccountProgressRequest
{
    public int CurrentStageKey { get; init; }
    public int CurrentStatusKey { get; init; }
    public int? RiskLevelKey { get; init; }
    public int? AccountTypeKey { get; init; }
    public int? SORKey { get; init; }
    public string? OwnerName { get; init; }
    public string? BusinessUnit { get; init; }
    public DateTime? TargetRemediationDate { get; init; }
    public DateTime? ActualCompletionDate { get; init; }
    public string? Notes { get; init; }
    public string? Reason { get; init; }
    public int? ExceptionKey { get; init; }
}
