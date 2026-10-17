using System.Net;
using System.Net.Http.Json;
using BlueTrack.Api.Controllers;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-243: repeated refused token requests from one address are throttled.
/// Its own class, so its own host and cache: the failures counted here can't
/// spill into the other API access tests.
/// </summary>
public class ApiTokenThrottleTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public ApiTokenThrottleTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TooManyRefusals_AreAnswered429_WithRetryAfter()
    {
        var client = _factory.CreateClient();
        for (var i = 0; i < ApiTokenController.MaxFailuresPerMinute; i++)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/token", new { apiKey = "bt_notarealkeynotarealkey" })).StatusCode);
        }

        var throttled = await client.PostAsJsonAsync("/api/auth/token", new { apiKey = "bt_notarealkeynotarealkey" });
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled.StatusCode);
        Assert.Equal("60", throttled.Headers.RetryAfter!.Delta!.Value.TotalSeconds.ToString());
    }
}
