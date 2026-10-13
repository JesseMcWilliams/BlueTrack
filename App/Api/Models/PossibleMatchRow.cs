namespace BlueTrack.Api.Models;

/// <summary>D-212 (phase 4c): one open possible match on the Possible Account Matches report.</summary>
public sealed class PossibleMatchRow
{
    public long ReconciliationKey { get; init; }
    public required string MatchMethod { get; init; }
    public string? Reason { get; init; }
    public DateTime MatchedDate { get; init; }
    public long FeedAccountKey { get; init; }
    public string? FeedUserName { get; init; }
    public string? FeedAddress { get; init; }
    public required string FeedSourceName { get; init; }
    public long CyberArkAccountKey { get; init; }
    public required string CyberArkAccountName { get; init; }
    public string? CyberArkUserName { get; init; }
    public string? CyberArkAddress { get; init; }
    public required string CyberArkSourceName { get; init; }
}
