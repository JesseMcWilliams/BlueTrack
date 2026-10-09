namespace BlueTrack.Api.Models;

/// <summary>
/// The full risk_exception row shape needed by the create/edit form --
/// unlike RiskExceptionSummary, this carries the raw scope keys
/// (AccountKey/ApplicationKey) rather than a resolved display name.
/// </summary>
public sealed class RiskExceptionDetail
{
    public int ExceptionKey { get; init; }
    public required string ExceptionID { get; init; }
    public long? AccountKey { get; init; }
    public int? ApplicationKey { get; init; }
    public required string Justification { get; init; }

    /// <summary>UserKey who approved this exception -- used by the Risk Exception segregation-of-duties check (AccountProgressController.Update).</summary>
    /// <summary>The approving BlueTrack user; null for an exception imported from another tool (D-183), which names its approver in ApprovedByName.</summary>
    public int? ApprovedBy { get; init; }
    public string? ApprovedByName { get; init; }
    public DateTime ApprovalDate { get; init; }
    public DateTime ReviewDate { get; init; }
    public required string StatusName { get; init; }
    public string? ExternalTicketReference { get; init; }

    // D-183: where an imported exception came from.
    public string? SourceTool { get; init; }
    public string? SourceExceptionId { get; init; }
    public string? SourceUrl { get; init; }
    public string? ImportedByName { get; init; }
    public DateTime? ImportedDate { get; init; }
}

/// <summary>D-183: one row of the Risk Exceptions import, validated and resolved.</summary>
public sealed class ImportedRiskException
{
    public long? AccountKey { get; init; }
    public int? ApplicationKey { get; init; }
    public required string Justification { get; init; }
    public required string ApprovedByName { get; init; }
    public DateTime ApprovalDate { get; init; }
    public DateTime ReviewDate { get; init; }
    public required string StatusName { get; init; }
    public string? ExternalTicketReference { get; init; }
    public required string SourceTool { get; init; }
    public required string SourceExceptionId { get; init; }
    public string? SourceUrl { get; init; }
}

/// <summary>D-183: result of the Risk Exceptions import -- per-row errors, not all-or-nothing.</summary>
public sealed class RiskExceptionImportResult
{
    public int TotalRows { get; init; }
    public int CreatedCount { get; init; }
    public int LinkedCount { get; init; }
    public required IReadOnlyList<ImportedExceptionId> Created { get; init; }
    public required IReadOnlyList<ImportRowError> Errors { get; init; }
}

/// <summary>The BlueTrack ExceptionID a row was given.</summary>
public sealed class ImportedExceptionId
{
    public int RowNumber { get; init; }
    public required string ExceptionId { get; init; }
    public required string SourceExceptionId { get; init; }
}
