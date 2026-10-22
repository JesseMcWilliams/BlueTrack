using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-294: the number and date format on Global Application Configuration. Only en-US is ever saved here: the culture
/// is process-wide, and other tests running in parallel format their expected text with the default.
/// </summary>
public class FormatCultureConfigTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public FormatCultureConfigTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Admin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");
        return client;
    }

    private static JsonObject With(JsonObject original, Action<JsonObject> change)
    {
        var copy = JsonNode.Parse(original.ToJsonString())!.AsObject();
        change(copy);
        return copy;
    }

    [Fact]
    public async Task FormatCulture_DefaultsToEnUs_IsValidated_AndSavedByItsOwnName()
    {
        var client = Admin();
        var original = (await client.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!;
        Assert.Equal("en-US", original["formatCulture"]!.GetValue<string>());

        foreach (var bad in new[] { "en", "not a culture!", "" })
        {
            var rejected = await client.PutAsJsonAsync("/api/admin/configuration", With(original, c => { c["formatCulture"] = bad; }));
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            Assert.Contains("number and date format", (await rejected.Content.ReadFromJsonAsync<JsonObject>())!["title"]!.GetValue<string>());
        }

        var saved = await client.PutAsJsonAsync("/api/admin/configuration", With(original, c => { c["formatCulture"] = "en-us"; }));
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        Assert.Equal("en-US", (await client.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!["formatCulture"]!.GetValue<string>());

        // Leaving it out keeps it.
        var unchanged = await client.PutAsJsonAsync("/api/admin/configuration", With(original, c => { c.Remove("formatCulture"); }));
        Assert.Equal(HttpStatusCode.NoContent, unchanged.StatusCode);
        Assert.Equal("en-US", (await client.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!["formatCulture"]!.GetValue<string>());
    }
}
