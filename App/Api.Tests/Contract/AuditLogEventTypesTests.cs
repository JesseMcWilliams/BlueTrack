using System.Net;
using System.Net.Http.Json;
using BlueTrack.Api.Tests.Integration;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>UI review W11-1: GET /api/audit-log/event-types lists the event type names, under ViewAuditLog like the log itself.</summary>
public class AuditLogEventTypesTests(BlueTrackWebApplicationFactory factory) : IClassFixture<BlueTrackWebApplicationFactory>
{
    private HttpClient Client(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    [Fact]
    public async Task EventTypes_ListsTheNames_Sorted()
    {
        var names = await Client("TestUser.Viewer").GetFromJsonAsync<List<string>>("/api/audit-log/event-types");

        Assert.NotNull(names);
        Assert.Contains("FieldEdit", names);
        Assert.Equal(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names.ToList());
    }

    [Fact]
    public async Task EventTypes_WithoutViewAuditLog_IsForbidden()
    {
        var response = await Client("TestUser.DoesNotExist").GetAsync("/api/audit-log/event-types");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }
}
