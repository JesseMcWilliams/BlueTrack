using System.Collections.Concurrent;
using Microsoft.IdentityModel.Tokens;

namespace BlueTrack.Api.Auth;

/// <summary>
/// D-198: remembers each SAML assertion this server has accepted until it
/// expires, so a captured SAML response can't be posted again to sign in
/// (ITfoxtec's DetectReplayedTokens is off by default). In memory: BlueTrack
/// runs as one in-process API on one server; a restart forgets, which only
/// reopens the window for assertions issued before it, and those expire in
/// minutes.
/// </summary>
public sealed class SamlTokenReplayCache(TimeProvider timeProvider) : ITokenReplayCache
{
    private readonly ConcurrentDictionary<string, DateTime> seen = new(StringComparer.Ordinal);

    public bool TryAdd(string securityToken, DateTime expiresOn)
    {
        Prune();
        return seen.TryAdd(securityToken, expiresOn);
    }

    public bool TryFind(string securityToken) =>
        seen.TryGetValue(securityToken, out var expiresOn) && expiresOn > timeProvider.GetUtcNow().UtcDateTime;

    private void Prune()
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        foreach (var (token, expiresOn) in seen)
        {
            if (expiresOn <= now) seen.TryRemove(token, out _);
        }
    }
}
