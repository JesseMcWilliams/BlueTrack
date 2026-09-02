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
    public DateTime ApprovalDate { get; init; }
    public DateTime ReviewDate { get; init; }
    public required string StatusName { get; init; }
    public string? ExternalTicketReference { get; init; }
}
