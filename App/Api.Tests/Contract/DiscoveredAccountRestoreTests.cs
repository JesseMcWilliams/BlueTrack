using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>UI review W5-4: a dismissed discovered account is listed with dismissed=true and Restore makes it New again; Restore refuses a New one.</summary>
public class DiscoveredAccountRestoreTests(BlueTrackWebApplicationFactory factory) : IClassFixture<BlueTrackWebApplicationFactory>
{
    private HttpClient Client(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    [Fact]
    public async Task DismissThenRestore_MovesTheRowBetweenTheTwoViews()
    {
        var admin = Client("TestUser.Admin");
        var rows = (await admin.GetFromJsonAsync<JsonArray>("/api/reports/discovered-accounts?page=1&pageSize=50"))!;
        Assert.NotEmpty(rows);
        var key = rows[0]!["discoveredAccountKey"]!.GetValue<int>();
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/reports/discovered-accounts/{key}/dismiss", null)).StatusCode);

            var dismissed = (await admin.GetFromJsonAsync<JsonArray>("/api/reports/discovered-accounts?dismissed=true&page=1&pageSize=500"))!;
            Assert.Contains(dismissed, r => r!["discoveredAccountKey"]!.GetValue<int>() == key);
            var open = (await admin.GetFromJsonAsync<JsonArray>("/api/reports/discovered-accounts?page=1&pageSize=500"))!;
            Assert.DoesNotContain(open, r => r!["discoveredAccountKey"]!.GetValue<int>() == key);

            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/reports/discovered-accounts/{key}/restore", null)).StatusCode);
            open = (await admin.GetFromJsonAsync<JsonArray>("/api/reports/discovered-accounts?page=1&pageSize=500"))!;
            Assert.Contains(open, r => r!["discoveredAccountKey"]!.GetValue<int>() == key);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/reports/discovered-accounts/{key}/restore", null)).StatusCode);
        }
        finally
        {
            await admin.PostAsync($"/api/reports/discovered-accounts/{key}/restore", null); // leave the seeded row as it was
        }
    }

    [Fact]
    public async Task Restore_UnknownRow_IsNotFound_AndAViewerIsForbidden()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Client("TestUser.Admin").PostAsync("/api/reports/discovered-accounts/999999999/restore", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.Viewer").PostAsync("/api/reports/discovered-accounts/999999999/restore", null)).StatusCode);
    }
}
