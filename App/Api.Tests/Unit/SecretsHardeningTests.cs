using BlueTrack.Api.Secrets;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-202: HTTPS-only backend URLs and the expiring, configuration-keyed fallback cache.</summary>
public class SecretsHardeningTests
{
    private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Theory]
    [InlineData("CyberArkCCP", """{"BaseUrl":"http://pvwa.example","AppId":"A"}""", "BaseUrl")]
    [InlineData("CyberArkConjur", """{"applianceUrl":"http://conjur.example","Account":"a","Login":"l"}""", "ApplianceUrl")]
    [InlineData("AzureKeyVault", """{"VaultUri":"not a url"}""", "VaultUri")]
    public void Validate_RejectsANonHttpsUrl(string backendType, string settings, string field)
    {
        var error = SecretsBackendUrls.Validate(backendType, settings);
        Assert.NotNull(error);
        Assert.StartsWith(field, error);
    }

    [Theory]
    [InlineData("CyberArkCCP", """{"BaseUrl":"https://pvwa.example","AppId":"A"}""")]
    [InlineData("CyberArkCCP", """{"BaseUrl":"","AppId":"A"}""")]  // blank: the provider's own "not configured"
    [InlineData("CyberArkCP", """{"AppId":"A"}""")]                // no URL at all
    [InlineData("WindowsDpapi", null)]
    [InlineData("CyberArkConjur", "{not json")]                     // the provider reports bad JSON itself
    public void Validate_AcceptsHttpsBlankOrNoUrl(string backendType, string? settings)
    {
        Assert.Null(SecretsBackendUrls.Validate(backendType, settings));
    }

    [Fact]
    public void FallbackCache_ServesAnEntryUpToMaxAge_ThenNot()
    {
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero));
        var cache = new SecretFallbackCache(clock);
        var key = SecretFallbackCache.Key("CyberArkCCP", "App", "https://pvwa.example", new SecretQuery("S", "Root", "O"));
        cache.Store(key, new SecretResult("pw".ToCharArray(), "user", "host", FromFallbackCache: false));

        clock.Now += SecretFallbackCache.MaxAge;
        Assert.True(cache.TryGetFallback(key, out var hit));
        Assert.True(hit.FromFallbackCache);
        Assert.Equal("pw", new string(hit.Content));

        clock.Now += TimeSpan.FromSeconds(1);
        Assert.False(cache.TryGetFallback(key, out _));
    }

    [Fact]
    public void FallbackCache_KeyIncludesAppIdAndUrl_AndClearEmptiesIt()
    {
        var cache = new SecretFallbackCache(TimeProvider.System);
        var query = new SecretQuery("S", "Root", "O");
        var key = SecretFallbackCache.Key("CyberArkCCP", "App", "https://pvwa.example", query);
        cache.Store(key, new SecretResult("pw".ToCharArray(), null, null, FromFallbackCache: false));

        Assert.False(cache.TryGetFallback(SecretFallbackCache.Key("CyberArkCCP", "OtherApp", "https://pvwa.example", query), out _));
        Assert.False(cache.TryGetFallback(SecretFallbackCache.Key("CyberArkCCP", "App", "https://other.example", query), out _));

        cache.Clear();
        Assert.False(cache.TryGetFallback(key, out _));
    }
}
