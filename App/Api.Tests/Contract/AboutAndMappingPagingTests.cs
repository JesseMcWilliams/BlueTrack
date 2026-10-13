using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-213: the user menu's About endpoint, and paging on the Application ↔ Safe Mapping lists.</summary>
public class AboutAndMappingPagingTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public AboutAndMappingPagingTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    [Fact]
    public async Task About_AnySignedInUser_GetsTheVersionWithoutTheCommit()
    {
        var about = await Client("TestUser.Viewer").GetFromJsonAsync<JsonObject>("/api/about");

        var version = about!["version"]!.GetValue<string>();
        Assert.Matches(@"^\d+\.\d+\.\d+", version);
        Assert.DoesNotContain("+", version);
    }

    [Theory]
    [InlineData("/api/safes")]
    [InlineData("/api/applications/detailed")]
    public async Task MappingLists_PageWithCountHeaders_OnlyWhenAPageIsAskedFor(string url)
    {
        var client = Client("TestUser.Admin");

        var all = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, all.StatusCode);
        Assert.False(all.Headers.Contains("X-Total-Count"));
        var allRows = (await all.Content.ReadFromJsonAsync<JsonArray>())!;

        var paged = await client.GetAsync($"{url}?page=1&pageSize=1");
        Assert.Equal(allRows.Count.ToString(), Assert.Single(paged.Headers.GetValues("X-Total-Count")));
        Assert.Equal(Math.Min(1, allRows.Count), (await paged.Content.ReadFromJsonAsync<JsonArray>())!.Count);
    }
}
