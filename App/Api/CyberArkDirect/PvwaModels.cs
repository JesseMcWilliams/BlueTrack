namespace BlueTrack.Api.CyberArkDirect;

// D-248: what Self-Hosted Direct reads from PVWA's REST API (PAM Self-Hosted 14.2 and later, the 2nd gen API under
// /PasswordVault/API). Times are UTC; PVWA sends epoch seconds, except a Safe's lastModificationTime (microseconds).
// D-270: everything REST returns is kept (for the stg_shd_* tables), not only what the Load reads; the positional
// members are what the EVD-shaped stg_sh_* tables need, the init-only members the rest.

/// <summary>A Safe the service user can see. SafeNumber is the EVD's CASSafeID.</summary>
public sealed record PvwaSafe(
    long SafeNumber, string SafeUrlId, string SafeName, string? Location, string? CreatorName, DateTime? CreationTime,
    int? DaysRetention, int? VersionsRetention)
{
    public string? Description { get; init; }
    public string? CreatorId { get; init; }
    public bool? OlacEnabled { get; init; }
    public string? ManagingCpm { get; init; }
    public bool? AutoPurgeEnabled { get; init; }
    public DateTime? LastModificationTime { get; init; }
    public bool? IsExpiredMember { get; init; }
}

/// <summary>
/// An account ("password object"). The REST id is <c>SafeNumber_FileId</c>, the EVD's CAFSafeID and CAFFileID
/// (confirmed against the lab, 2026-10-19), so the Load's SourceAccountId is the same either way.
/// </summary>
public sealed record PvwaAccount(
    long SafeNumber, long FileId, string SafeName, string Name, string? Address, string? UserName, string? PlatformId,
    DateTime? CreatedTime, DateTime? LastModifiedTime, DateTime? LastVerifiedTime, bool? AutoManaged, string? ManualReason,
    IReadOnlyDictionary<string, string> Properties)
{
    public string Id => $"{SafeNumber}_{FileId}";
    public string? SecretType { get; init; }
    public DateTime? CategoryModificationTime { get; init; }

    /// <summary>secretManagement.status as sent: present only once the CPM has acted on the account (the lab: lowercase success/failure).</summary>
    public string? CpmStatus { get; init; }
    public DateTime? LastReconciledTime { get; init; }
    public string? RemoteMachines { get; init; }
    public bool? AccessRestrictedToRemoteMachines { get; init; }

    /// <summary>The latest activity of each CPM action code, read only for an account with a <see cref="CpmStatus"/>.</summary>
    public IReadOnlyList<PvwaCpmActivity> CpmActivity { get; init; } = [];
}

/// <summary>One CPM action from the account's activity log (classic API): its code, name and when it last happened.</summary>
public sealed record PvwaCpmActivity(int Code, string? Activity, DateTime? Time, string? UserName, string? Reason, string? MoreInfo);

/// <summary>A Safe member: a user or a group, with PVWA's named permissions (22 of them on 14.2).</summary>
public sealed record PvwaMember(
    long SafeNumber, string SafeName, long MemberId, string MemberName, bool IsGroup, DateTime? ExpirationDate,
    IReadOnlyDictionary<string, bool> Permissions)
{
    public string? SafeUrlId { get; init; }
    public string? MemberType { get; init; }
    public bool? IsExpiredMembershipEnable { get; init; }

    /// <summary>A built-in Vault user or group (Administrator, Auditors, ...). The EVD-shaped staging leaves these out, as the pull always did.</summary>
    public bool? IsPredefinedUser { get; init; }
    public bool? IsReadOnly { get; init; }
}

public sealed record PvwaUser(
    long Id, string UserName, string? Source, string? UserType, string? Location, bool Enabled,
    string? FirstName, string? LastName, string? Email)
{
    public bool? ComponentUser { get; init; }
    public bool? Suspended { get; init; }
    public string? MiddleName { get; init; }

    /// <summary>Whether the details call ran and answered; the fields below come from it.</summary>
    public bool DetailsRead { get; init; }
    public string? Organization { get; init; }
    public string? Department { get; init; }
    public string? Title { get; init; }
    public string? Profession { get; init; }
    public string? HomeEmail { get; init; }
    public string? OtherEmail { get; init; }
    public string? HomePage { get; init; }
    public string? UserDn { get; init; }
    public string? DistinguishedName { get; init; }
    public string? Description { get; init; }
    public DateTime? ExpiryDate { get; init; }
    public DateTime? LastSuccessfulLoginDate { get; init; }
    public bool? PasswordNeverExpires { get; init; }
    public bool? ChangePassOnNextLogon { get; init; }

    /// <summary>Vault authorizations, unauthorized interfaces and authentication methods, as (kind, value).</summary>
    public IReadOnlyList<(string Kind, string Value)> Authorizations { get; init; } = [];
}

public sealed record PvwaGroup(long Id, string Name, string? GroupType, string? Location, string? Description, IReadOnlyList<long> MemberIds)
{
    public string? Directory { get; init; }
    public string? Dn { get; init; }
    public IReadOnlyDictionary<long, string> MemberNames { get; init; } = new Dictionary<long, string>();
}

public sealed record PvwaPlatform(string Id, string? Name, string? Description, bool? Active, string? PlatformType)
{
    public string? SystemType { get; init; }
    public string? PlatformBaseId { get; init; }
    public string? AllowedSafes { get; init; }
    public bool? PerformPeriodicVerification { get; init; }
    public int? VerifyEveryXDays { get; init; }
    public bool? AllowManualVerification { get; init; }
    public bool? PerformPeriodicChange { get; init; }
    public int? ChangeEveryXDays { get; init; }
    public bool? AllowManualChange { get; init; }
    public bool? AutomaticReconcileWhenUnsynched { get; init; }
    public bool? AllowManualReconciliation { get; init; }
    public bool? RequirePsm { get; init; }
    public bool? RecordSessions { get; init; }
    public string? PsmServerId { get; init; }
    public bool? RequireDualControl { get; init; }
    public bool? EnforceExclusiveAccess { get; init; }
    public bool? EnforceOneTimePassword { get; init; }

    /// <summary>From Platforms/Targets; null for a platform that isn't a target platform or when that call was refused.</summary>
    public bool? RequireReasonForAccess { get; init; }
    public bool? VerifyOnAdd { get; init; }
    public bool? ChangeOnAdd { get; init; }
    public bool? ChangePasswordInResetMode { get; init; }

    /// <summary>Required and optional account properties and linked account types, as (kind, name, display name).</summary>
    public IReadOnlyList<(string Kind, string Name, string? DisplayName)> Properties { get; init; } = [];
}

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
