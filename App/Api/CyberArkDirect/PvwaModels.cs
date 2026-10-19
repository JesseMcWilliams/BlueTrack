namespace BlueTrack.Api.CyberArkDirect;

// D-248: what Self-Hosted Direct reads from PVWA's REST API (PAM Self-Hosted 14.2 and later, the 2nd gen API under
// /PasswordVault/API). Times are UTC; PVWA sends epoch seconds, except a Safe's lastModificationTime (microseconds),
// which isn't read. Only what the Load needs is kept.

/// <summary>A Safe the service user can see. SafeNumber is the EVD's CASSafeID.</summary>
public sealed record PvwaSafe(
    long SafeNumber, string SafeUrlId, string SafeName, string? Location, string? CreatorName, DateTime? CreationTime,
    int? DaysRetention, int? VersionsRetention);

/// <summary>
/// An account ("password object"). The REST id is <c>SafeNumber_FileId</c>, the EVD's CAFSafeID and CAFFileID
/// (confirmed against the lab, 2026-10-19), so the Load's SourceAccountId is the same either way.
/// </summary>
public sealed record PvwaAccount(
    long SafeNumber, long FileId, string SafeName, string Name, string? Address, string? UserName, string? PlatformId,
    DateTime? CreatedTime, DateTime? LastModifiedTime, DateTime? LastVerifiedTime, bool? AutoManaged, string? ManualReason,
    IReadOnlyDictionary<string, string> Properties);

/// <summary>A Safe member: a user or a group, with PVWA's named permissions (22 of them on 14.2).</summary>
public sealed record PvwaMember(
    long SafeNumber, string SafeName, long MemberId, string MemberName, bool IsGroup, DateTime? ExpirationDate,
    IReadOnlyDictionary<string, bool> Permissions);

public sealed record PvwaUser(
    long Id, string UserName, string? Source, string? UserType, string? Location, bool Enabled,
    string? FirstName, string? LastName, string? Email);

public sealed record PvwaGroup(long Id, string Name, string? GroupType, string? Location, string? Description, IReadOnlyList<long> MemberIds);

public sealed record PvwaPlatform(string Id, string? Name, string? Description, bool? Active, string? PlatformType);

/// <summary>Everything one pull read, plus anything that went wrong without stopping it.</summary>
public sealed class PvwaSnapshot
{
    public List<PvwaSafe> Safes { get; } = [];
    public List<PvwaAccount> Accounts { get; } = [];
    public List<PvwaMember> Members { get; } = [];
    public List<PvwaUser> Users { get; } = [];
    public List<PvwaGroup> Groups { get; } = [];
    public List<PvwaPlatform> Platforms { get; } = [];

    /// <summary>Non-fatal problems (an account id that didn't parse, a user whose details couldn't be read); shown with the pull.</summary>
    public List<string> Warnings { get; } = [];
}

/// <summary>A PVWA call failed in a way the pull can't continue from (the message is safe to show an admin).</summary>
public sealed class PvwaException(string message, Exception? inner = null) : Exception(message, inner);
