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
}

/// <summary>
/// Body for PUT /api/account-progress/{accountKey}. Reason is required only
/// when regressing CurrentStageKey to a lower StageOrder (D-51) -- the API
/// enforces this, the field isn't blanket-required.
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
}
