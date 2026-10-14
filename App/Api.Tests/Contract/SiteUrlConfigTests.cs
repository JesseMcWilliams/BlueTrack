using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-224: the Site URL on Global Application Configuration -- validated, stored normalized, audited, and cleared with "".</summary>
public class SiteUrlConfigTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public SiteUrlConfigTests(BlueTrackWebApplicationFactory factory)
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
    public async Task SiteUrl_IsValidated_SavedNormalized_Audited_AndClearedWithBlank()
    {
        var client = Admin();
        var original = (await client.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!;
        var host = $"siteurl-{Guid.NewGuid():N}.example.com";

        try
        {
            foreach (var bad in new[] { "http://" + host, "not a url", $"https://{host}/?x=1" })
            {
                var rejected = await client.PutAsJsonAsync("/api/admin/configuration", With(original, c => { c["siteUrl"] = bad; }));
                Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
                Assert.Contains("site URL", (await rejected.Content.ReadFromJsonAsync<JsonObject>())!["title"]!.GetValue<string>());
            }

            var saved = await client.PutAsJsonAsync("/api/admin/configuration", With(original, c => { c["siteUrl"] = $"  HTTPS://{host.ToUpperInvariant()}:443/ "; }));
            Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
            var after = (await client.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!;
            Assert.Equal($"https://{host}", after["siteUrl"]!.GetValue<string>());

            await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
            {
                var audited = await connection.QuerySingleAsync<int>("""
                    SELECT COUNT(*) FROM web.audit_field_change fc
                    JOIN web.audit_event e ON e.AuditEventKey = fc.AuditEventKey
                    WHERE e.EntityName = 'app_config' AND fc.FieldName = 'SiteUrl' AND fc.NewValue = @value
                    """, new { value = $"https://{host}" });
                Assert.Equal(1, audited);
            }

            // Leaving it out (null) keeps it; "" clears it.
            var unchanged = await client.PutAsJsonAsync("/api/admin/configuration", With(after, c => { c.Remove("siteUrl"); }));
            Assert.Equal(HttpStatusCode.NoContent, unchanged.StatusCode);
            Assert.Equal($"https://{host}", (await client.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!["siteUrl"]!.GetValue<string>());

            var cleared = await client.PutAsJsonAsync("/api/admin/configuration", With(after, c => { c["siteUrl"] = ""; }));
            Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
            Assert.Null((await client.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!["siteUrl"]);
        }
        finally
        {
            var restore = await client.PutAsJsonAsync("/api/admin/configuration", With(original, c => { c["siteUrl"] = original["siteUrl"]?.GetValue<string>() ?? ""; }));
            Assert.Equal(HttpStatusCode.NoContent, restore.StatusCode);
        }
    }
}
