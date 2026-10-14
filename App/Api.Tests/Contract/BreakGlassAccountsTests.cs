using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-216: break-glass accounts -- the list's API, and a designated account's sign-in writing a BreakGlassLogon audit event.</summary>
public class BreakGlassAccountsTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public BreakGlassAccountsTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private sealed record Created(int BreakGlassAccountKey, string ExternalIdentifier);

    [Fact]
    public async Task Api_IsForIdentityProviderAdmins_AndResolvesALocalWindowsAccountToItsSid()
    {
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.Viewer").GetAsync("/api/admin/break-glass-accounts")).StatusCode);

        var admin = Client("TestUser.Admin");
        var unknown = await admin.PostAsJsonAsync("/api/admin/break-glass-accounts", new { account = $@".\NoSuchUser{Guid.NewGuid():N}"[..20] });
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        // The built-in local Administrator's SID always ends in -500.
        var response = await admin.PostAsJsonAsync("/api/admin/break-glass-accounts", new { account = @".\Administrator", notes = "Contract test" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<Created>())!;
        try
        {
            Assert.Matches(@"^S-1-5-21-[\d-]+-500$", created.ExternalIdentifier);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/break-glass-accounts", new { account = created.ExternalIdentifier })).StatusCode);
            var list = (await admin.GetFromJsonAsync<JsonArray>("/api/admin/break-glass-accounts"))!;
            Assert.Contains(list, a => a!["breakGlassAccountKey"]!.GetValue<int>() == created.BreakGlassAccountKey && a["displayName"]!.GetValue<string>() == @".\Administrator");
        }
        finally
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/break-glass-accounts/{created.BreakGlassAccountKey}")).StatusCode);
        }
    }

    [Fact]
    public async Task SignIn_ByADesignatedAccount_WritesABreakGlassLogonEvent()
    {
        var admin = Client("TestUser.Admin");
        var viewer = Client("TestUser.Viewer");
        var identifier = (await viewer.GetFromJsonAsync<JsonObject>("/api/me"))!["externalIdentifier"]!.GetValue<string>();
        var userKey = await TestUsers.GetUserKeyAsync(identifier);

        var created = (await (await admin.PostAsJsonAsync("/api/admin/break-glass-accounts", new { account = identifier, displayName = "CT break-glass" }))
            .Content.ReadFromJsonAsync<Created>())!;
        try
        {
            // A sign-in is a rights-cache miss (D-82); clear the cache so the next request is one.
            using (var scope = _factory.Services.CreateScope())
            {
                await scope.ServiceProvider.GetRequiredService<BlueTrack.Api.Auth.UserRightsCache>().InvalidateAllAsync();
            }
            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            const string countSql = """
                SELECT COUNT(*) FROM web.audit_event e JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey
                WHERE t.EventTypeName = 'BreakGlassLogon' AND e.PerformedByUserKey = @userKey
                """;
            var before = await connection.QuerySingleAsync<int>(countSql, new { userKey });

            Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/me")).StatusCode);

            Assert.Equal(before + 1, await connection.QuerySingleAsync<int>(countSql, new { userKey }));
        }
        finally
        {
            await admin.DeleteAsync($"/api/admin/break-glass-accounts/{created.BreakGlassAccountKey}");
        }
    }
}
