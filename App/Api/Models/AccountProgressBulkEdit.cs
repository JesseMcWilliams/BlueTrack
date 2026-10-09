namespace BlueTrack.Api.Models;

/// <summary>
/// D-182: one bulk edit on Account Progress -- the same values for the
/// chosen fields on every listed account. Only the fields named in Fields
/// change; every other field of each account is left as it is.
/// </summary>
public sealed class BulkEditAccountProgressRequest
{
    public required IReadOnlyList<long> AccountKeys { get; init; }

    /// <summary>Which fields to change: names from AccountProgressBulkEditService.EditableFields.</summary>
    public required IReadOnlyList<string> Fields { get; init; }

    public int? CurrentStageKey { get; init; }
    public int? CurrentStatusKey { get; init; }
    public int? RiskLevelKey { get; init; }
    public int? AccountTypeKey { get; init; }
    public int? SORKey { get; init; }
    public string? OwnerName { get; init; }
    public string? BusinessUnit { get; init; }
    public DateTime? TargetRemediationDate { get; init; }
    public DateTime? ActualCompletionDate { get; init; }
    public string? Notes { get; init; }

    /// <summary>"Replace" (default) or "Append": add Notes on a new line after each account's existing notes.</summary>
    public string? NotesMode { get; init; }

    /// <summary>Used where the bulk edit moves an account to an earlier stage (D-51), and recorded on the summary audit event.</summary>
    public string? Reason { get; init; }
}

public sealed class BulkEditAccountProgressResult
{
    public int Requested { get; init; }
    public int Updated { get; init; }
    public int Unchanged { get; init; }
    public required IReadOnlyList<BulkEditSkippedAccount> Skipped { get; init; }
}

public sealed class BulkEditSkippedAccount
{
    public long AccountKey { get; init; }
    public string? AccountName { get; init; }
    public required string Reason { get; init; }
}

/// <summary>"Select all matching": the keys of every account matching the list's filters, when there are no more than the limit.</summary>
public sealed class AccountProgressKeysResult
{
    public int MatchingCount { get; init; }
    public int MaxAccounts { get; init; }
    public required IReadOnlyList<long> AccountKeys { get; init; }
}
