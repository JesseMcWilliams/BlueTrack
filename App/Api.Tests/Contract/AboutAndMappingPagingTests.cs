using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using BlueTrack.Api.Controllers;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-213/D-226: the user menu's About endpoint (and the Deployment page's matching version), and paging on the Application ↔ Safe Mapping lists.</summary>
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
    public async Task About_AnySignedInUser_GetsTheBuildStampedVersion()
    {
        var about = await Client("TestUser.Viewer").GetFromJsonAsync<JsonObject>("/api/about");

        // D-226: the build's informational version, as stamped from git (or <Version> without git).
        var version = about!["version"]!.GetValue<string>();
        var informational = typeof(AboutController).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(informational, version);
        Assert.Matches(@"^\d+\.\d+\.\d+(-dev(\.\d+)?\+[0-9a-f]{7,40}(\.dirty)?)?$", version);
    }

    [Fact]
    public async Task DeploymentPage_ShowsTheSameVersionAsAbout()
    {
        var client = Client("TestUser.Admin");

        var about = await client.GetFromJsonAsync<JsonObject>("/api/about");
        var deployment = await client.GetFromJsonAsync<JsonObject>("/api/admin/deployment");

        Assert.Equal(about!["version"]!.GetValue<string>(), deployment!["version"]!.GetValue<string>());
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
