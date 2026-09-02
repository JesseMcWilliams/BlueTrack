namespace BlueTrack.Api.Models;

/// <summary>web.account_progress_lock (D-50), joined for display.</summary>
public sealed class AccountProgressLockStatus
{
    public long AccountKey { get; init; }
    public int LockedByUserKey { get; init; }
    public string? LockedByName { get; init; }
    public DateTime LockedAt { get; init; }
    public DateTime LastHeartbeatAt { get; init; }
}
