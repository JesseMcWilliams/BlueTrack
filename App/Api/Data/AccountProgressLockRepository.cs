using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Pessimistic locking for the Account Progress edit form (D-50). The
/// abandoned-lock timeout comes from web.app_config.LockTimeoutMinutes
/// (admin-configurable, added by 08_BlueTrack_WebSchema.sql).
/// </summary>
public sealed class AccountProgressLockRepository(IDbConnectionFactory connectionFactory)
{
    /// <summary>
    /// The current, live lock, or null. D-174: a lock with no heartbeat
    /// within LockTimeoutMinutes counts as abandoned and is reported as no
    /// lock. Previously it was reported as held, so AccountProgressDetail.vue
    /// (which only acquires when the status is unlocked) never called
    /// TryAcquireAsync -- the only place a stale row is deleted -- and an
    /// abandoned lock blocked everyone, its own holder included, until an
    /// admin force-released it.
    /// </summary>
    public async Task<AccountProgressLockStatus?> GetStatusAsync(long accountKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<AccountProgressLockStatus>(StatusSql, new { AccountKey = accountKey });
    }

    /// <summary>
    /// Whether this user holds the lock row, live or not -- the save check
    /// (AccountProgressController.Update). Deliberately ignores staleness
    /// (D-174): a holder whose heartbeat lapsed can still save as long as
    /// nobody else has taken the lock over; once someone has, TryAcquireAsync
    /// has replaced the row and this returns false.
    /// </summary>
    public async Task<bool> IsHeldByAsync(long accountKey, int userKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteScalarAsync<bool>(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM web.account_progress_lock WHERE AccountKey = @AccountKey AND LockedByUserKey = @UserKey) THEN 1 ELSE 0 END",
            new { AccountKey = accountKey, UserKey = userKey });
    }

    /// <summary>
    /// Clears a stale lock (no heartbeat within LockTimeoutMinutes), then
    /// tries to acquire. Returns the resulting lock status either way --
    /// the caller compares LockedByUserKey to the requesting user to tell
    /// "I got it" from "someone else already holds it".
    /// </summary>
    public async Task<AccountProgressLockStatus?> TryAcquireAsync(long accountKey, int userKey)
    {
        using var connection = connectionFactory.Create();

        await connection.ExecuteAsync("""
            DELETE FROM web.account_progress_lock
            WHERE AccountKey = @AccountKey
              AND LastHeartbeatAt < DATEADD(MINUTE, -(SELECT LockTimeoutMinutes FROM web.app_config), SYSUTCDATETIME())
            """, new { AccountKey = accountKey });

        await connection.ExecuteAsync("""
            INSERT INTO web.account_progress_lock (AccountKey, LockedByUserKey, LockedAt, LastHeartbeatAt)
            SELECT @AccountKey, @UserKey, SYSUTCDATETIME(), SYSUTCDATETIME()
            WHERE NOT EXISTS (SELECT 1 FROM web.account_progress_lock WHERE AccountKey = @AccountKey)
            """, new { AccountKey = accountKey, UserKey = userKey });

        return await connection.QuerySingleOrDefaultAsync<AccountProgressLockStatus>(StatusSql, new { AccountKey = accountKey });
    }

    public async Task<bool> HeartbeatAsync(long accountKey, int userKey)
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.ExecuteAsync("""
            UPDATE web.account_progress_lock SET LastHeartbeatAt = SYSUTCDATETIME()
            WHERE AccountKey = @AccountKey AND LockedByUserKey = @UserKey
            """, new { AccountKey = accountKey, UserKey = userKey });
        return rows > 0;
    }

    public async Task ReleaseAsync(long accountKey, int userKey)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(
            "DELETE FROM web.account_progress_lock WHERE AccountKey = @AccountKey AND LockedByUserKey = @UserKey",
            new { AccountKey = accountKey, UserKey = userKey });
    }

    /// <summary>Admin force-break (D-50) -- releases regardless of who holds it.</summary>
    public async Task ForceReleaseAsync(long accountKey)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(
            "DELETE FROM web.account_progress_lock WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
    }

    private const string StatusSql = """
        SELECT l.AccountKey, l.LockedByUserKey, u.DisplayName AS LockedByName, l.LockedAt, l.LastHeartbeatAt
        FROM web.account_progress_lock l
        LEFT JOIN web.app_user u ON u.UserKey = l.LockedByUserKey
        WHERE l.AccountKey = @AccountKey
          AND l.LastHeartbeatAt >= DATEADD(MINUTE, -(SELECT LockTimeoutMinutes FROM web.app_config), SYSUTCDATETIME())
        """;
}
