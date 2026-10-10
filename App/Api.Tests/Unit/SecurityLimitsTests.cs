using System.Text;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Errors;
using BlueTrack.Api.RiskScoring;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-198: the CSV row limit and the SAML replay cache.</summary>
public class SecurityLimitsTests
{
    private sealed class SettableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static MemoryStream Csv(int dataRows)
    {
        var text = new StringBuilder("Name\n");
        for (var i = 0; i < dataRows; i++) text.Append('r').Append(i).Append('\n');
        return new MemoryStream(Encoding.UTF8.GetBytes(text.ToString()));
    }

    [Fact]
    public async Task CsvReader_AcceptsUpToTheLimit_AndRefusesMore()
    {
        Assert.Equal(CsvFileReader.MaxRows, (await CsvFileReader.ReadRowsAsync(Csv(CsvFileReader.MaxRows))).Count);

        var tooMany = await Assert.ThrowsAsync<ApiProblemException>(() => CsvFileReader.ReadRowsAsync(Csv(CsvFileReader.MaxRows + 1)));
        Assert.Equal(400, tooMany.Status);
    }

    [Fact]
    public void SamlReplayCache_AcceptsAnAssertionOnce_UntilItExpires()
    {
        var clock = new SettableClock(new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero));
        var cache = new SamlTokenReplayCache(clock);
        var expires = clock.GetUtcNow().UtcDateTime.AddMinutes(5);

        Assert.True(cache.TryAdd("assertion-1", expires));
        Assert.True(cache.TryFind("assertion-1"));
        Assert.False(cache.TryAdd("assertion-1", expires));

        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.False(cache.TryFind("assertion-1"));
        Assert.True(cache.TryAdd("assertion-1", clock.GetUtcNow().UtcDateTime.AddMinutes(5)));
    }
}
