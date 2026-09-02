using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Pessimistic locking for the Account Progress edit form (D-50). The
/// abandoned-lock timeout comes from web.app_config.LockTimeoutMinutes
/// (admin-configurable, added by 15_BlueTrack_LockTimeoutConfig.sql).
/// </summary>
public sealed class AccountProgressLockRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<AccountProgressLockStatus?> GetStatusAsync(long accountKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<AccountProgressLockStatus>(StatusSql, new { AccountKey = accountKey });
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
        """;
}
