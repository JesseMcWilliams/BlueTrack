using System.Net;
using System.Net.Sockets;

namespace BlueTrack.Api.DataFeeds;

/// <summary>D-212 (phase 4c): DNS lookups for PossibleMatchFinder, behind an interface so tests can answer them.</summary>
public interface IHostResolver
{
    /// <summary>The host's addresses (an IP address resolves to itself); empty when it can't be resolved.</summary>
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);

    /// <summary>The address's host name (reverse lookup); null when there is none.</summary>
    Task<string?> ReverseAsync(IPAddress address, CancellationToken cancellationToken);
}

/// <summary>The app server's own DNS, as the App Pool's account, each lookup limited to a few seconds.</summary>
public sealed class DnsHostResolver(ILogger<DnsHostResolver> logger) : IHostResolver
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var address)) return [address];
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            return await Dns.GetHostAddressesAsync(host, timeout.Token);
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            logger.LogDebug(ex, "DNS lookup of {Host} failed.", host);
            return [];
        }
    }

    public async Task<string?> ReverseAsync(IPAddress address, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            return (await Dns.GetHostEntryAsync(address.ToString(), timeout.Token)).HostName;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException or ArgumentException)
        {
            logger.LogDebug(ex, "Reverse DNS lookup of {Address} failed.", address);
            return null;
        }
    }
}

/// <summary>
/// D-212 (phase 4c): pairs a feed account with a CyberArk account that may be
/// the same account but didn't match exactly (phase 4b links only exact
/// matches). Both must have the same user name (ignoring case and spaces);
/// their addresses then agree in one of three ways, tried in this order:
///   - FeedShortName: the same host name once the domain is dropped
///     (web01 and web01.corp.example.com), ignoring case.
///   - FeedDnsForward: both resolve to a shared IP address (an IP address
///     counts as itself), e.g. a CNAME and the real name.
///   - FeedDnsReverse: one is an IP address whose reverse lookup gives the
///     other's name (in full or short form).
/// Lookups are cached for one run; each pair is only a proposal for a person
/// to link or dismiss on the Possible Account Matches report.
/// </summary>
public sealed class PossibleMatchFinder(IHostResolver resolver)
{
    public sealed record Account(long AccountKey, string UserName, string Address);
    public sealed record Candidate(long FeedAccountKey, long CyberArkAccountKey, string MatchMethod, string Reason);

    private readonly Dictionary<string, Task<IReadOnlyList<IPAddress>>> forward = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<IPAddress, Task<string?>> reverse = [];

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    /// <summary>The host name without its domain; null for an IP address.</summary>
    public static string? ShortName(string address)
    {
        var a = Normalize(address).TrimEnd('.');
        if (IPAddress.TryParse(a, out _)) return null;
        var dot = a.IndexOf('.');
        return dot < 0 ? a : a[..dot];
    }

    public async Task<IReadOnlyList<Candidate>> FindAsync(IEnumerable<Account> feedAccounts, IEnumerable<Account> cyberArkAccounts, CancellationToken cancellationToken = default)
    {
        var byUser = cyberArkAccounts.GroupBy(a => Normalize(a.UserName)).ToDictionary(g => g.Key, g => g.ToList());
        var candidates = new List<Candidate>();

        foreach (var feed in feedAccounts)
        {
            if (!byUser.TryGetValue(Normalize(feed.UserName), out var sameUser)) continue;
            foreach (var cyberArk in sameUser)
            {
                if (Normalize(feed.Address) == Normalize(cyberArk.Address)) continue;   // exact: phase 4b's job
                if (await CompareAsync(feed.Address, cyberArk.Address, cancellationToken) is { } match)
                {
                    candidates.Add(new Candidate(feed.AccountKey, cyberArk.AccountKey, match.Method, match.Reason));
                }
            }
        }
        return candidates;
    }

    private async Task<(string Method, string Reason)?> CompareAsync(string feedAddress, string cyberArkAddress, CancellationToken cancellationToken)
    {
        var feedShort = ShortName(feedAddress);
        if (feedShort is not null && feedShort == ShortName(cyberArkAddress))
        {
            return ("FeedShortName", $"'{feedAddress.Trim()}' and '{cyberArkAddress.Trim()}' have the same host name, '{feedShort}'.");
        }

        var feedIps = await ResolveAsync(feedAddress, cancellationToken);
        var cyberArkIps = await ResolveAsync(cyberArkAddress, cancellationToken);
        var shared = feedIps.Intersect(cyberArkIps).FirstOrDefault();
        if (shared is not null)
        {
            return ("FeedDnsForward", $"'{feedAddress.Trim()}' and '{cyberArkAddress.Trim()}' both resolve to {shared}.");
        }

        foreach (var (ipText, other) in new[] { (feedAddress, cyberArkAddress), (cyberArkAddress, feedAddress) })
        {
            if (!IPAddress.TryParse(ipText.Trim(), out var ip) || ShortName(other) is not { } otherShort) continue;
            var name = await ReverseAsync(ip, cancellationToken);
            if (name is not null && (Normalize(name).TrimEnd('.') == Normalize(other).TrimEnd('.') || ShortName(name) == otherShort))
            {
                return ("FeedDnsReverse", $"{ip} reverse-resolves to '{name}', matching '{other.Trim()}'.");
            }
        }
        return null;
    }

    private Task<IReadOnlyList<IPAddress>> ResolveAsync(string address, CancellationToken cancellationToken)
    {
        var key = Normalize(address);
        if (!forward.TryGetValue(key, out var lookup))
        {
            lookup = resolver.ResolveAsync(key, cancellationToken);
            forward[key] = lookup;
        }
        return lookup;
    }

    private Task<string?> ReverseAsync(IPAddress address, CancellationToken cancellationToken)
    {
        if (!reverse.TryGetValue(address, out var lookup))
        {
            lookup = resolver.ReverseAsync(address, cancellationToken);
            reverse[address] = lookup;
        }
        return lookup;
    }
}
