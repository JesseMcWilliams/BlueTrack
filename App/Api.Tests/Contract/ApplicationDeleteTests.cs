using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// UI review W8-5 and W8-2: an application can be deleted when nothing uses it (and is refused, with the counts, when
/// a Safe does); the detailed list carries those counts; the Safes list can be searched and narrowed to Safes with no
/// application.
/// </summary>
public class ApplicationDeleteTests(BlueTrackWebApplicationFactory factory) : IClassFixture<BlueTrackWebApplicationFactory>
{
    private HttpClient Client(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private static async Task<int> CreateApplicationAsync(HttpClient client)
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var response = await client.PostAsJsonAsync("/api/applications", new { applicationCode = "DEL" + tag, applicationName = "Delete test " + tag });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!["applicationKey"]!.GetValue<int>();
    }

    [Fact]
    public async Task Delete_AnUnusedApplication_RemovesIt()
    {
        var client = Client("TestUser.Admin");
        var key = await CreateApplicationAsync(client);

        var response = await client.DeleteAsync($"/api/applications/{key}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var detailed = await client.GetFromJsonAsync<JsonArray>("/api/applications/detailed");
        Assert.DoesNotContain(detailed!, row => row!["applicationKey"]!.GetValue<int>() == key);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/applications/{key}")).StatusCode);
    }

    [Fact]
    public async Task Delete_AnApplicationASafeUses_IsRefusedWithTheCounts_UntilTheSafeIsMoved()
    {
        var client = Client("TestUser.Admin");
        var key = await CreateApplicationAsync(client);
        var safes = await client.GetFromJsonAsync<JsonArray>("/api/safes");
        var safe = safes![0]!;
        var safeKey = safe["safeKey"]!.GetValue<int>();
        var original = safe["applicationKey"]?.GetValue<int>();
        try
        {
            (await client.PutAsJsonAsync($"/api/safes/{safeKey}/application", key)).EnsureSuccessStatusCode();

            var refused = await client.DeleteAsync($"/api/applications/{key}");
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("1 Safe(s)", await refused.Content.ReadAsStringAsync());
            var row = (await client.GetFromJsonAsync<JsonArray>("/api/applications/detailed"))!.Single(r => r!["applicationKey"]!.GetValue<int>() == key)!;
            Assert.Equal(1, row["safeCount"]!.GetValue<int>());
        }
        finally
        {
            (await client.PutAsJsonAsync<int?>($"/api/safes/{safeKey}/application", original)).EnsureSuccessStatusCode();
        }

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/applications/{key}")).StatusCode);
    }

    [Fact]
    public async Task Delete_WithoutCurateApplicationMapping_IsForbidden()
    {
        var response = await Client("TestUser.Viewer").DeleteAsync("/api/applications/1");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Safes_CanBeSearchedByName_AndNarrowedToThoseWithNoApplication()
    {
        var client = Client("TestUser.Admin");
        var all = (await client.GetFromJsonAsync<JsonArray>("/api/safes"))!;
        var name = all[0]!["safeName"]!.GetValue<string>();

        var searched = await client.GetAsync($"/api/safes?page=1&pageSize=50&search={Uri.EscapeDataString(name)}");
        var rows = (await searched.Content.ReadFromJsonAsync<JsonArray>())!;
        Assert.NotEmpty(rows);
        Assert.All(rows, r => Assert.Contains(name, r!["safeName"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase));
        Assert.Equal(rows.Count.ToString(), Assert.Single(searched.Headers.GetValues("X-Filtered-Count")));

        var unassigned = await client.GetAsync("/api/safes?page=1&pageSize=500&unassigned=true");
        var unassignedRows = (await unassigned.Content.ReadFromJsonAsync<JsonArray>())!;
        Assert.All(unassignedRows, r => Assert.Null(r!["applicationKey"]));
        Assert.Equal(all.Count.ToString(), Assert.Single(unassigned.Headers.GetValues("X-Total-Count")));
    }
}
