using System.Text.Json;
using Dapper;
using Microsoft.Extensions.Caching.Distributed;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Auth;

/// <summary>
/// D-13/D-82: caches a resolved UserRights per identity so
/// UserRightsResolver doesn't re-run the group->role->permission
/// resolution on every single request. Backed by
/// Microsoft.Extensions.Caching.SqlServer (web.distributed_cache) -- the
/// user's choice of a distributed cache over an in-process one, even
/// though D-09 only confirms a single server today.
///
/// This is BlueTrack's "session" for D-13/D-14's purposes: a per-identity
/// cache entry, not an ASP.NET Core cookie-based Session. Windows
/// Negotiate doesn't need cookie/session-ID tracking for anything else
/// this app does, so none was introduced just for this.
/// </summary>
public sealed class UserRightsCache(IDistributedCache cache, AppConfigRepository appConfigRepository, IDbConnectionFactory connectionFactory)
{
    // D-197: the longest anyone keeps rights they've lost. The sliding idle
    // timeout alone never expired for someone who kept working, so a removed
    // AD group or role permission lingered indefinitely.
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(15);
    private const string KeyPrefix = "rights:";

    public async Task<UserRights?> GetAsync(int providerKey, string externalIdentifier)
    {
        var raw = await cache.GetStringAsync(BuildKey(providerKey, externalIdentifier));
        return raw is null ? null : JsonSerializer.Deserialize<UserRights>(raw);
    }

    public async Task SetAsync(int providerKey, string externalIdentifier, UserRights rights)
    {
        // Tied to the existing admin-configurable idle timeout (D-28) --
        // there was no separate "how long to cache rights" setting to add,
        // and the two concepts (session idle timeout, cached-rights
        // lifetime) are reasonably the same thing in an app with no other
        // session state.
        var config = await appConfigRepository.GetAsync();
        var options = new DistributedCacheEntryOptions
        {
            SlidingExpiration = TimeSpan.FromMinutes(config.IdleTimeoutMinutes),
            AbsoluteExpirationRelativeToNow = MaximumAge
        };
        await cache.SetStringAsync(BuildKey(providerKey, externalIdentifier), JsonSerializer.Serialize(rights), options);
    }

    /// <summary>Admin-triggered Reload Rights for another user (D-14): invalidate only -- their own next request re-resolves live via their own Negotiate token, the same as self-service.</summary>
    public Task InvalidateAsync(int providerKey, string externalIdentifier) =>
        cache.RemoveAsync(BuildKey(providerKey, externalIdentifier));

    /// <summary>
    /// D-197: drops every user's cached rights, so a change to a role's
    /// permissions, a group mapping or an identity provider applies on each
    /// user's next request. The cache is SQL Server's (web.distributed_cache,
    /// Program.cs), which has no "remove by prefix", hence the direct delete.
    /// </summary>
    public async Task InvalidateAllAsync()
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("DELETE FROM web.distributed_cache WHERE Id LIKE @Prefix", new { Prefix = KeyPrefix + "%" });
    }

    private static string BuildKey(int providerKey, string externalIdentifier) => $"{KeyPrefix}{providerKey}:{externalIdentifier}";
}
