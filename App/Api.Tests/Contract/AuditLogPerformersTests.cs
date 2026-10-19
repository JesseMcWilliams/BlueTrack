using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>UI review W11-1: GET /api/audit-log/performers?q= finds people who appear in the log, under ViewAuditLog.</summary>
public class AuditLogPerformersTests(BlueTrackWebApplicationFactory factory) : IClassFixture<BlueTrackWebApplicationFactory>
{
    private HttpClient Client(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    [Fact]
    public async Task Performers_FindsAPersonWhoHasActed_ByPartOfTheirName()
    {
        var viewer = Client("TestUser.Viewer");
        // Some event was done by someone: the log's own first row names them.
        var events = await viewer.GetFromJsonAsync<JsonArray>("/api/audit-log?page=1&pageSize=50");
        var name = events!.Select(e => e!["performedByName"]?.GetValue<string>()).First(n => !string.IsNullOrWhiteSpace(n))!;
        var fragment = name[..Math.Min(4, name.Length)];

        var found = await viewer.GetFromJsonAsync<JsonArray>($"/api/audit-log/performers?q={Uri.EscapeDataString(fragment)}");

        Assert.Contains(found!, p => p!["displayName"]!.GetValue<string>() == name);
        Assert.True(found!.Count <= 10);
    }

    [Fact]
    public async Task Performers_WithoutViewAuditLog_IsForbidden()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.DoesNotExist").GetAsync("/api/audit-log/performers?q=a")).StatusCode);
    }
}
