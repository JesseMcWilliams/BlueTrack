namespace BlueTrack.Api.Models;

/// <summary>
/// One row across the Risk Exceptions list/approval/overdue-review pages
/// (Design_Risk_Exception_Tracking.md). ScopeType/ScopeName describe
/// whichever of AccountKey/ApplicationKey is actually set (exactly one
/// always is, enforced at the application layer per D-18/Q-25).
/// </summary>
public sealed class RiskExceptionSummary
{
    public int ExceptionKey { get; init; }
    public required string ExceptionID { get; init; }
    public required string ScopeType { get; init; } // "Account" or "Application"
    public required string ScopeName { get; init; }
    public required string Justification { get; init; }
    public string? ApprovedByName { get; init; }
    public DateTime ApprovalDate { get; init; }
    public DateTime ReviewDate { get; init; }
    public required string StatusName { get; init; }
    public string? ExternalTicketReference { get; init; }
}
