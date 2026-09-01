namespace BlueTrack.Api.Models;

/// <summary>
/// One row for the Account Progress list/grid page (Design_Application_Structure.md).
/// A read projection, not the full fact_account_progress shape.
/// </summary>
public sealed class AccountProgressSummary
{
    public long AccountKey { get; init; }
    public required string AccountName { get; init; }
    public required string StageName { get; init; }
    public required string StatusName { get; init; }
    public string? RiskLevelName { get; init; }
    public string? OwnerName { get; init; }
    public DateTime? TargetRemediationDate { get; init; }
    public DateTime? ActualCompletionDate { get; init; }
}
