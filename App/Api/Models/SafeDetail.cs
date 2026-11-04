namespace BlueTrack.Api.Models;

/// <summary>
/// D-308: GET /api/safes/{safeKey} -- one Safe's page: what it is, the BlueTrack application mapped to it (D-31),
/// whether the decommission or ignore patterns name it, and how many of its accounts are tracked.
/// </summary>
public sealed class SafeDetail
{
    public int SafeKey { get; init; }
    public required string SafeName { get; init; }
    public string? Description { get; init; }
    public string? SourceSystemName { get; init; }
    public string? ManagingCPM { get; init; }
    public DateTime? CreatedDate { get; init; }
    public int? ApplicationKey { get; init; }
    public string? ApplicationCode { get; init; }
    public string? ApplicationName { get; init; }
    public string? ApplicationOwnerName { get; init; }
    public bool IsDecomFlagged { get; init; }
    public bool IsIgnored { get; init; }
    public bool IsInternal { get; init; }
    public int ActiveAccountCount { get; init; }
    public int DeletedAccountCount { get; init; }
}
