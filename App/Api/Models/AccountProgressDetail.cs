namespace BlueTrack.Api.Models;

/// <summary>
/// The full editable fact_account_progress row shape for the Account
/// Progress edit form. AccountName/ExceptionKey are read-only context, not
/// directly editable here -- ExceptionKey is set by the Risk Exception
/// workflow, not hand-typed (see Design_Risk-Exception-Tracking.md).
/// </summary>
public sealed class AccountProgressDetail
{
    public long ProgressKey { get; init; }
    public long AccountKey { get; init; }
    public required string AccountName { get; init; }

    /// <summary>D-133/D-134: shown alongside AccountName on the edit screen -- fact_account.UserName/Address, read-only context same as AccountName (not part of the editable fact_account_progress field set).</summary>
    public string? UserName { get; init; }
    public string? Address { get; init; }

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

/// <summary>
/// Body for PUT /api/account-progress/{accountKey}. Reason is required only
/// when regressing CurrentStageKey to a lower StageOrder (D-51) -- the API
/// enforces this, the field isn't blanket-required. ExceptionKey is
/// required only when CurrentStatusKey resolves to "Risk Accepted /
/// Excluded" (Design_Risk-Exception-Tracking.md workflow step 2) -- ignored
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

/// <summary>D-185: delete or undelete accounts in BlueTrack; a reason is always required.</summary>
public sealed class AccountDeletionRequest
{
    public required IReadOnlyList<long> AccountKeys { get; init; }
    public required string Reason { get; init; }
}

public sealed class AccountDeletionResult
{
    public int Requested { get; init; }
    public int Changed { get; init; }
    public required IReadOnlyList<BulkEditSkippedAccount> Skipped { get; init; }
}

/// <summary>D-185: one row of an account's delete/undelete history.</summary>
public sealed class AccountDeletionHistoryEntry
{
    public required string Action { get; init; }
    public required string Reason { get; init; }
    public string? PerformedByName { get; init; }
    public DateTime PerformedAt { get; init; }
    public string? BatchId { get; init; }
}
