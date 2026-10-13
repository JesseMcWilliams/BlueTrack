using System.Net;
using BlueTrack.Api.DataFeeds;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-212 (phase 4c): the loose comparison behind the Possible Account Matches report, with DNS answered by a fake.</summary>
public class PossibleMatchFinderTests
{
    private sealed class FakeResolver(Dictionary<string, string[]> forward, Dictionary<string, string>? reverse = null) : IHostResolver
    {
        public List<string> Lookups { get; } = [];

        public Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Lookups.Add(host);
            if (IPAddress.TryParse(host, out var ip)) return Task.FromResult<IReadOnlyList<IPAddress>>([ip]);
            return Task.FromResult<IReadOnlyList<IPAddress>>(
                forward.TryGetValue(host, out var ips) ? ips.Select(IPAddress.Parse).ToList() : []);
        }

        public Task<string?> ReverseAsync(IPAddress address, CancellationToken cancellationToken) =>
            Task.FromResult(reverse?.GetValueOrDefault(address.ToString()));
    }

    private static PossibleMatchFinder.Account A(long key, string user, string address) => new(key, user, address);

    [Theory]
    [InlineData("web01.corp.example.com", "web01")]
    [InlineData("WEB01.", "web01")]
    [InlineData("10.1.2.3", null)]
    public void ShortName_DropsTheDomain_AndIsNullForAnIpAddress(string address, string? expected) =>
        Assert.Equal(expected, PossibleMatchFinder.ShortName(address));

    [Fact]
    public async Task SameShortName_IsProposed_WithoutDns()
    {
        var resolver = new FakeResolver([]);
        var found = await new PossibleMatchFinder(resolver).FindAsync(
            [A(1, "root", "web01")], [A(100, " ROOT ", "web01.corp.example.com")]);

        var match = Assert.Single(found);
        Assert.Equal((1, 100, "FeedShortName"), (match.FeedAccountKey, match.CyberArkAccountKey, match.MatchMethod));
        Assert.Empty(resolver.Lookups);
    }

    [Fact]
    public async Task SharedIpAddress_IsProposed_AndEachHostIsLookedUpOnce()
    {
        var resolver = new FakeResolver(new() { ["app-alias.example.com"] = ["10.0.0.5"], ["app01.example.com"] = ["10.0.0.5", "10.0.0.6"] });
        var found = await new PossibleMatchFinder(resolver).FindAsync(
            [A(1, "svc", "app-alias.example.com"), A(2, "svc2", "app-alias.example.com")],
            [A(100, "svc", "app01.example.com"), A(101, "svc2", "app01.example.com")]);

        Assert.Equal(["FeedDnsForward", "FeedDnsForward"], found.Select(f => f.MatchMethod));
        Assert.Contains("10.0.0.5", found[0].Reason);
        Assert.Equal(2, resolver.Lookups.Count);
    }

    [Fact]
    public async Task IpAddressWhoseReverseNameMatches_IsProposed()
    {
        var resolver = new FakeResolver([], new() { ["10.9.9.9"] = "db01.corp.example.com" });
        var found = await new PossibleMatchFinder(resolver).FindAsync([A(1, "sa", "10.9.9.9")], [A(100, "sa", "db01")]);

        Assert.Equal("FeedDnsReverse", Assert.Single(found).MatchMethod);
    }

    [Fact]
    public async Task ExactMatches_DifferentUsers_AndUnrelatedHosts_AreNotProposed()
    {
        var resolver = new FakeResolver(new() { ["a.example.com"] = ["10.0.0.1"], ["b.example.com"] = ["10.0.0.2"] });
        var found = await new PossibleMatchFinder(resolver).FindAsync(
            [A(1, "root", "host1.example.com"), A(2, "admin", "web01"), A(3, "svc", "a.example.com")],
            [A(100, "ROOT", "HOST1.example.com"), A(101, "other", "web01.example.com"), A(102, "svc", "b.example.com")]);

        Assert.Empty(found);
    }
}
