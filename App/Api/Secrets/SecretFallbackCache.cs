using System.Collections.Concurrent;

namespace BlueTrack.Api.Secrets;

/// <summary>
/// D-49's fetch-first, cache-as-fallback store for CyberArk CP and CCP: every
/// successful fetch is kept, and a kept value is returned only when a live
/// request fails with a transient error (D-48: vault connectivity, or a
/// password change in progress).
///
/// D-202 (2026-10-12): previously a static dictionary per provider, keyed on
/// Safe|Folder|Object only, never expiring. Now:
///   - an entry older than <see cref="MaxAge"/> is never served;
///   - the key includes the backend type, AppID and URL, so after an admin
///     points the backend somewhere else a failure can't return the old
///     configuration's secret;
///   - saving the secrets backend clears it (SecretsStoreController).
/// In memory only, per process. Singleton.
/// </summary>
public sealed class SecretFallbackCache(TimeProvider timeProvider)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, (SecretResult Result, DateTimeOffset StoredAt)> entries = new();

    public static string Key(string backendType, string appId, string? baseUrl, SecretQuery query) =>
        string.Join('\u001f', backendType, appId, baseUrl ?? "", query.Safe, query.Folder, query.Object);

    public void Store(string key, SecretResult result) => entries[key] = (result, timeProvider.GetUtcNow());

    /// <summary>The kept value, marked FromFallbackCache, if there is one younger than MaxAge.</summary>
    public bool TryGetFallback(string key, out SecretResult result)
    {
        if (entries.TryGetValue(key, out var entry))
        {
            if (timeProvider.GetUtcNow() - entry.StoredAt <= MaxAge)
            {
                result = entry.Result with { FromFallbackCache = true };
                return true;
            }
            entries.TryRemove(key, out _);
        }
        result = null!;
        return false;
    }

    public void Clear() => entries.Clear();
}
