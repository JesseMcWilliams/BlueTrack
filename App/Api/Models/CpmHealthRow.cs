namespace BlueTrack.Api.Models;

/// <summary>D-288: one account on Reports > CPM Health (dbo.vw_cpm_health): its CPM status is failure. Times are UTC.</summary>
public sealed class CpmHealthRow
{
    public long AccountKey { get; init; }
    public required string AccountName { get; init; }
    public string? UserName { get; init; }
    public string? Address { get; init; }
    public string? SafeName { get; init; }
    public string? PlatformId { get; init; }
    public string? SourceSystemName { get; init; }
    public string? CPMStatus { get; init; }
    /// <summary>When the load first saw it failing.</summary>
    public DateTime? CpmFailureSince { get; init; }
    public bool? AutoManaged { get; init; }
    public string? ManualReason { get; init; }
    public string? LastCPMAction { get; init; }
    public string? LastCPMActionResult { get; init; }
    public DateTime? LastCPMActionTime { get; init; }
    public DateTime? LastVerifiedDate { get; init; }
    public DateTime? LastReconciledDate { get; init; }
    public DateTime? LastCPMModifiedDate { get; init; }
    public string? OwnerName { get; init; }
    public string? StageName { get; init; }
    public string? StatusName { get; init; }
}
