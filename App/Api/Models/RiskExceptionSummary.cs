namespace BlueTrack.Api.Models;

/// <summary>
/// One row across the Risk Exceptions list/approval/overdue-review pages
/// (Design_Risk-Exception-Tracking.md). ScopeType/ScopeName describe
/// whichever of AccountKey/ApplicationKey is actually set (exactly one
/// always is, enforced at the application layer per D-18/Q-25).
/// </summary>
public sealed class RiskExceptionSummary
{
    public int ExceptionKey { get; init; }
    public required string ExceptionID { get; init; }
    public required string ScopeType { get; init; } // "Account" or "Application"
    public required string ScopeName { get; init; }

    /// <summary>UI review W3-6: the scope's key, so the pages can link the account (null for an application).</summary>
    public long? AccountKey { get; init; }
    public int? ApplicationKey { get; init; }
    public required string Justification { get; init; }
    public string? ApprovedByName { get; init; }
    public DateTime ApprovalDate { get; init; }
    public DateTime ReviewDate { get; init; }
    public required string StatusName { get; init; }
    public string? ExternalTicketReference { get; init; }

    // D-183: where an imported exception came from.
    public string? SourceTool { get; init; }
    public string? SourceExceptionId { get; init; }
    public string? SourceUrl { get; init; }
}
