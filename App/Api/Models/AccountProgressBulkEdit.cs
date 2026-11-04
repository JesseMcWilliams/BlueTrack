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
    /// <summary>D-239: the account's own Application; null (when chosen) clears it.</summary>
    public int? ApplicationKey { get; init; }
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

/// <summary>
/// D-318: one row of the Accounts export, which the import reads back: the key, the account's identity (read only),
/// and the editable fields by name, as the edit page shows them.
/// </summary>
public sealed class AccountProgressExportRow
{
    public long AccountKey { get; init; }
    public string? UserName { get; init; }
    public string? Address { get; init; }
    public string? AccountName { get; init; }
    public string? SafeName { get; init; }
    public string? StageName { get; init; }
    public string? StatusName { get; init; }
    public string? RiskLevelName { get; init; }
    public string? AccountTypeName { get; init; }
    public string? SORName { get; init; }
    public string? ApplicationName { get; init; }
    public string? OwnerName { get; init; }
    public string? BusinessUnit { get; init; }
    public DateTime? TargetRemediationDate { get; init; }
    public DateTime? ActualCompletionDate { get; init; }
    public string? Notes { get; init; }
    /// <summary>D-323: the account's risk score override and its reason, which the import reads back too.</summary>
    public int? OverrideRiskScore { get; init; }
    public string? OverrideReason { get; init; }
}

/// <summary>D-318: what an Accounts import did: rows read, accounts updated or already as the file says, and the rows it couldn't apply.</summary>
public sealed class AccountProgressImportResult
{
    public int Rows { get; init; }
    public int Updated { get; init; }
    public int Unchanged { get; init; }
    public required IReadOnlyList<AccountProgressImportProblem> Problems { get; init; }
}

public sealed class AccountProgressImportProblem
{
    public int RowNumber { get; init; }
    public long? AccountKey { get; init; }
    public required string Reason { get; init; }
}
