using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.ApiAccess;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-243 (phase 1): API keys and the tokens they are exchanged for, through
/// the real HTTP pipeline. Each test makes its own uniquely named keys (and a
/// role where it needs one) and removes them afterwards.
/// </summary>
public class ApiAccessTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public ApiAccessTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");
        return client;
    }

    private HttpClient ViewerClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Viewer");
        return client;
    }

    /// <summary>A client that, like a script, sends no X-Requested-With and no test identity.</summary>
    private HttpClient ScriptClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove(BlueTrack.Api.Security.RequireApiRequestHeaderMiddleware.HeaderName);
        return client;
    }

    private sealed record CreatedKey(int ApiKeyKey, string ApiKey, string KeyPrefix, DateTime ExpiresAt);
    private sealed record TokenResponse(string Access_Token, string Token_Type, int Expires_In);
    private sealed record KeyListItem(int ApiKeyKey, string DisplayName, string KeyPrefix, string RoleName, bool IsEnabled);

    private static async Task<int> RoleKeyAsync(string roleName)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<int>("SELECT AppRoleKey FROM web.app_role WHERE RoleName = @roleName", new { roleName });
    }

    private static async Task DeleteKeysAsync(params int[] apiKeyKeys)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("DELETE FROM web.api_key WHERE ApiKeyKey IN @apiKeyKeys", new { apiKeyKeys });
    }

    private static async Task<CreatedKey> CreateKeyAsync(HttpClient admin, string roleName, int? lifetimeDays = null)
    {
        var response = await admin.PostAsJsonAsync("/api/admin/api-keys",
            new { displayName = $"CT key {Guid.NewGuid():N}"[..24], appRoleKey = await RoleKeyAsync(roleName), lifetimeDays });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CreatedKey>())!;
    }

    private static async Task<string> ExchangeAsync(HttpClient client, string apiKey)
    {
        var response = await client.PostAsJsonAsync("/api/auth/token", new { apiKey });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var token = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("Bearer", token["token_type"]!.GetValue<string>());
        Assert.Equal(600, token["expires_in"]!.GetValue<int>());
        return token["access_token"]!.GetValue<string>();
    }

    private static HttpRequestMessage Get(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Create_ValidatesTheRequest_ShowsTheKeyOnce_AndNeverStoresIt()
    {
        var admin = AdminClient();
        var created = await CreateKeyAsync(admin, "Viewer");
        try
        {
            Assert.StartsWith("bt_", created.ApiKey);
            Assert.Equal(created.ApiKey[..11], created.KeyPrefix);
            Assert.InRange(created.ExpiresAt, DateTime.UtcNow.AddDays(89), DateTime.UtcNow.AddDays(91));   // the 90-day default

            var list = (await admin.GetFromJsonAsync<List<KeyListItem>>("/api/admin/api-keys"))!;
            var item = list.Single(k => k.ApiKeyKey == created.ApiKeyKey);
            Assert.Equal(created.KeyPrefix, item.KeyPrefix);
            Assert.Equal("Viewer", item.RoleName);
            Assert.DoesNotContain(created.ApiKey, await admin.GetStringAsync("/api/admin/api-keys"));

            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            var stored = await connection.QuerySingleAsync<byte[]>("SELECT KeyHash FROM web.api_key WHERE ApiKeyKey = @Key", new { Key = created.ApiKeyKey });
            Assert.Equal(ApiKeyGenerator.Hash(created.ApiKey), stored);

            var viewer = await RoleKeyAsync("Viewer");
            var adminRole = await RoleKeyAsync("Admin");
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/api-keys", new { displayName = "", appRoleKey = viewer })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/api-keys", new { displayName = "CT admin key", appRoleKey = adminRole })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/api-keys", new { displayName = "CT no role", appRoleKey = 0 })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/api-keys", new { displayName = "CT long", appRoleKey = viewer, lifetimeDays = 366 })).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/api-keys", new { displayName = item.DisplayName, appRoleKey = viewer })).StatusCode);

            var options = (await admin.GetFromJsonAsync<JsonObject>("/api/admin/api-keys/options"))!;
            Assert.DoesNotContain("Admin", options["roles"]!.AsArray().Select(r => r!["roleName"]!.GetValue<string>()));
            Assert.Equal(90, options["defaultLifetimeDays"]!.GetValue<int>());
        }
        finally
        {
            await DeleteKeysAsync(created.ApiKeyKey);
        }
    }

    [Fact]
    public async Task KeyManagement_NeedsTheManageApiKeysPermission()
    {
        var viewer = ViewerClient();
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/admin/api-keys")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/admin/api-keys", new { displayName = "x", appRoleKey = 1 })).StatusCode);
    }

    [Fact]
    public async Task Token_ExchangedForAKey_ActsAsTheKeysRole_WithoutBrowserHeaders()
    {
        var admin = AdminClient();
        var created = await CreateKeyAsync(admin, "Viewer");
        try
        {
            var script = ScriptClient();
            var token = await ExchangeAsync(script, created.ApiKey);

            var me = await script.SendAsync(Get("/api/me", token));
            Assert.Equal(HttpStatusCode.OK, me.StatusCode);
            var body = (await me.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Equal(["Viewer"], body["roleNames"]!.AsArray().Select(r => r!.GetValue<string>()));
            Assert.Contains("ViewDashboard", body["permissionNames"]!.AsArray().Select(p => p!.GetValue<string>()));
            Assert.StartsWith("API key: CT key", body["displayName"]!.GetValue<string>());

            // Its role can read, and key management and the admin pages are out of reach.
            Assert.Equal(HttpStatusCode.Forbidden, (await script.SendAsync(Get("/api/admin/api-keys", token))).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await script.SendAsync(Get("/api/admin/identity-providers", token))).StatusCode);

            // A write without X-Requested-With is judged by its permissions, not refused for the missing header.
            var post = new HttpRequestMessage(HttpMethod.Post, "/api/admin/api-keys") { Content = JsonContent.Create(new { displayName = "x", appRoleKey = 1 }) };
            post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var postResponse = await script.SendAsync(post);
            Assert.Equal(HttpStatusCode.Forbidden, postResponse.StatusCode);
            Assert.DoesNotContain("Missing request header", await postResponse.Content.ReadAsStringAsync());

            // Without a bearer header the check still applies.
            var plain = await script.PostAsJsonAsync("/api/admin/api-keys", new { displayName = "x", appRoleKey = 1 });
            Assert.Contains("Missing request header", await plain.Content.ReadAsStringAsync());

            // The exchange recorded the use.
            var item = (await admin.GetFromJsonAsync<List<KeyListItem>>("/api/admin/api-keys"))!.Single(k => k.ApiKeyKey == created.ApiKeyKey);
            Assert.True(item.IsEnabled);
            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            Assert.NotNull(await connection.QuerySingleAsync<DateTime?>("SELECT LastUsedAt FROM web.api_key WHERE ApiKeyKey = @Key", new { Key = created.ApiKeyKey }));
        }
        finally
        {
            await DeleteKeysAsync(created.ApiKeyKey);
        }
    }

    [Fact]
    public async Task Token_NeverCarriesThePermissionsAKeyCanNotHave()
    {
        var admin = AdminClient();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var roleName = $"CT role {Guid.NewGuid():N}"[..20];
        var roleKey = await connection.QuerySingleAsync<int>("INSERT INTO web.app_role (RoleName) OUTPUT inserted.AppRoleKey VALUES (@roleName)", new { roleName });
        await connection.ExecuteAsync("""
            INSERT INTO web.role_permission (RoleKey, PermissionKey)
            SELECT @roleKey, PermissionKey FROM web.app_permission WHERE PermissionName IN ('ViewDashboard', 'ManageApiKeys', 'ManageIdentityProviders', 'ReloadRights')
            """, new { roleKey });
        var created = await CreateKeyAsync(admin, roleName);
        try
        {
            var script = ScriptClient();
            var token = await ExchangeAsync(script, created.ApiKey);
            var me = (await (await script.SendAsync(Get("/api/me", token))).Content.ReadFromJsonAsync<JsonObject>())!;
            var permissions = me["permissionNames"]!.AsArray().Select(p => p!.GetValue<string>()).ToList();
            Assert.Equal(["ViewDashboard"], permissions);
            Assert.Equal(HttpStatusCode.Forbidden, (await script.SendAsync(Get("/api/admin/api-keys", token))).StatusCode);
        }
        finally
        {
            await DeleteKeysAsync(created.ApiKeyKey);
            await connection.ExecuteAsync("DELETE FROM web.role_permission WHERE RoleKey = @roleKey; DELETE FROM web.app_role WHERE AppRoleKey = @roleKey", new { roleKey });
        }
    }

    [Fact]
    public async Task Disabling_Deleting_OrExpiringAKey_StopsItsTokensAndExchanges()
    {
        var admin = AdminClient();
        var created = await CreateKeyAsync(admin, "Viewer");
        try
        {
            var script = ScriptClient();
            var token = await ExchangeAsync(script, created.ApiKey);
            Assert.Equal(HttpStatusCode.OK, (await script.SendAsync(Get("/api/me", token))).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/admin/api-keys/{created.ApiKeyKey}/disable", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await script.SendAsync(Get("/api/me", token))).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await script.PostAsJsonAsync("/api/auth/token", new { apiKey = created.ApiKey })).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/admin/api-keys/{created.ApiKeyKey}/enable", null)).StatusCode);
            var again = await ExchangeAsync(script, created.ApiKey);
            Assert.Equal(HttpStatusCode.OK, (await script.SendAsync(Get("/api/me", again))).StatusCode);

            await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
            {
                await connection.ExecuteAsync("UPDATE web.api_key SET ExpiresAt = DATEADD(MINUTE, -1, SYSUTCDATETIME()) WHERE ApiKeyKey = @Key", new { Key = created.ApiKeyKey });
            }
            Assert.Equal(HttpStatusCode.Unauthorized, (await script.PostAsJsonAsync("/api/auth/token", new { apiKey = created.ApiKey })).StatusCode);

            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/api-keys/{created.ApiKeyKey}")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await script.SendAsync(Get("/api/me", again))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/admin/api-keys/{created.ApiKeyKey}")).StatusCode);
        }
        finally
        {
            await DeleteKeysAsync(created.ApiKeyKey);
        }
    }

    [Fact]
    public async Task BadCredentials_AreRefusedUniformly_AndNeverFallBackToAnotherSignIn()
    {
        var admin = AdminClient();
        var created = await CreateKeyAsync(admin, "Viewer");
        try
        {
            var script = ScriptClient();
            var token = await ExchangeAsync(script, created.ApiKey);

            // Wrong key with a real prefix, and something that isn't a key at all: the same 401.
            var wrong = await script.PostAsJsonAsync("/api/auth/token", new { apiKey = created.KeyPrefix + "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx" });
            var junk = await script.PostAsJsonAsync("/api/auth/token", new { apiKey = "hello" });
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
            Assert.Equal(await wrong.Content.ReadAsStringAsync(), await junk.Content.ReadAsStringAsync());

            // A forged token, and an API key sent where a token belongs.
            Assert.Equal(HttpStatusCode.Unauthorized, (await script.SendAsync(Get("/api/me", token[..^3] + "abc"))).StatusCode);
            var keyAsToken = await script.SendAsync(Get("/api/me", created.ApiKey));
            Assert.Equal(HttpStatusCode.Unauthorized, keyAsToken.StatusCode);
            Assert.Contains("exchange it", await keyAsToken.Content.ReadAsStringAsync());

            // A bearer request is never answered as some other signed-in user, even if one is on the request.
            var impersonate = Get("/api/me", "not-a-token");
            impersonate.Headers.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");
            Assert.Equal(HttpStatusCode.Unauthorized, (await script.SendAsync(impersonate)).StatusCode);

            // Both refusals were audited, by prefix only.
            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            var details = (await connection.QueryAsync<string>("""
                SELECT TOP (10) e.Detail FROM web.audit_event e
                JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey
                WHERE t.EventTypeName = 'ApiTokenRejected' ORDER BY e.AuditEventKey DESC
                """)).ToList();
            Assert.Contains(details, d => d.Contains(created.KeyPrefix));
            Assert.Contains(details, d => d.Contains("(not a key)"));
            Assert.DoesNotContain(details, d => d.Contains(created.ApiKey));
        }
        finally
        {
            await DeleteKeysAsync(created.ApiKeyKey);
        }
    }

    [Fact]
    public async Task Exchange_IsAuditedWithoutTheKey()
    {
        var admin = AdminClient();
        var created = await CreateKeyAsync(admin, "Viewer");
        try
        {
            await ExchangeAsync(ScriptClient(), created.ApiKey);
            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            var details = (await connection.QueryAsync<string>("""
                SELECT TOP (20) e.Detail FROM web.audit_event e
                JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey
                WHERE t.EventTypeName IN ('ApiTokenIssued', 'ApiKeyCreated') ORDER BY e.AuditEventKey DESC
                """)).ToList();
            Assert.Contains(details, d => d.Contains("Token issued") && d.Contains(created.KeyPrefix));
            Assert.Contains(details, d => d.Contains("created with role Viewer") && d.Contains(created.KeyPrefix));
            Assert.DoesNotContain(details, d => d.Contains(created.ApiKey));
        }
        finally
        {
            await DeleteKeysAsync(created.ApiKeyKey);
        }
    }

    [Fact]
    public async Task ApiAccessProvider_IsNotASignInChoice_NorOnTheProvidersPage()
    {
        var admin = AdminClient();
        var providers = (await admin.GetFromJsonAsync<JsonArray>("/api/admin/identity-providers"))!;
        Assert.DoesNotContain("ApiAccess", providers.Select(p => p!["providerType"]!.GetValue<string>()));
    }

    [Fact]
    public async Task DefaultLifetime_IsASetting_ThatNewKeysFollow()
    {
        var admin = AdminClient();
        var original = (await admin.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!;
        Assert.Equal(90, original["apiKeyDefaultLifetimeDays"]!.GetValue<int>());

        JsonObject With(int days)
        {
            var body = JsonNode.Parse(original.ToJsonString())!.AsObject();
            body["apiKeyDefaultLifetimeDays"] = days;
            return body;
        }

        var created = (CreatedKey?)null;
        try
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/configuration", With(0))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/configuration", With(366))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/configuration", With(30))).StatusCode);

            created = await CreateKeyAsync(admin, "Viewer");
            Assert.InRange(created.ExpiresAt, DateTime.UtcNow.AddDays(29), DateTime.UtcNow.AddDays(31));
            var options = (await admin.GetFromJsonAsync<JsonObject>("/api/admin/api-keys/options"))!;
            Assert.Equal(30, options["defaultLifetimeDays"]!.GetValue<int>());
        }
        finally
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/configuration", With(90))).StatusCode);
            if (created is not null) await DeleteKeysAsync(created.ApiKeyKey);
        }
    }

    [Fact]
    public async Task Rotate_MakesANewKeyWithTheSameRole_AndLeavesTheOldOneWorking()
    {
        var admin = AdminClient();
        var original = await CreateKeyAsync(admin, "Auditor");
        CreatedKey? rotated = null;
        try
        {
            var response = await admin.PostAsync($"/api/admin/api-keys/{original.ApiKeyKey}/rotate", null);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
            rotated = new CreatedKey(body["apiKeyKey"]!.GetValue<int>(), body["apiKey"]!.GetValue<string>(), body["keyPrefix"]!.GetValue<string>(), body["expiresAt"]!.GetValue<DateTime>());
            Assert.NotEqual(original.ApiKey, rotated.ApiKey);
            Assert.Contains("(rotated ", body["displayName"]!.GetValue<string>());

            var list = (await admin.GetFromJsonAsync<List<KeyListItem>>("/api/admin/api-keys"))!;
            Assert.Equal("Auditor", list.Single(k => k.ApiKeyKey == rotated.ApiKeyKey).RoleName);
            Assert.True(list.Single(k => k.ApiKeyKey == original.ApiKeyKey).IsEnabled);

            var script = ScriptClient();
            await ExchangeAsync(script, original.ApiKey);
            await ExchangeAsync(script, rotated.ApiKey);

            // Rotating again the same day gets its own name.
            var again = await admin.PostAsync($"/api/admin/api-keys/{original.ApiKeyKey}/rotate", null);
            var againBody = (await again.Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.NotEqual(body["displayName"]!.GetValue<string>(), againBody["displayName"]!.GetValue<string>());
            await DeleteKeysAsync(againBody["apiKeyKey"]!.GetValue<int>());

            Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/admin/api-keys/2147483647/rotate", null)).StatusCode);
        }
        finally
        {
            await DeleteKeysAsync(original.ApiKeyKey);
            if (rotated is not null) await DeleteKeysAsync(rotated.ApiKeyKey);
        }
    }

    [Fact]
    public async Task RateLimit_IsASetting_AndTooManyRequestsAreAnswered429()
    {
        var admin = AdminClient();
        var original = (await admin.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!;
        Assert.Equal(600, original["apiRateLimitPerMinute"]!.GetValue<int>());

        JsonObject With(int limit)
        {
            var body = JsonNode.Parse(original.ToJsonString())!.AsObject();
            body["apiRateLimitPerMinute"] = limit;
            return body;
        }

        var created = await CreateKeyAsync(admin, "Viewer");
        try
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/configuration", With(0))).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/configuration", With(100001))).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/configuration", With(3))).StatusCode);

            var script = ScriptClient();
            var token = await ExchangeAsync(script, created.ApiKey);
            // A one-minute window can end between two calls, so allow for one change of window: 3 + 3 pass, the 7th can't.
            HttpResponseMessage? limited = null;
            for (var i = 0; i < 7 && limited is null; i++)
            {
                var response = await script.SendAsync(Get("/api/me", token));
                if (response.StatusCode == HttpStatusCode.TooManyRequests) limited = response;
                else Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
            Assert.NotNull(limited);
            Assert.InRange(limited!.Headers.RetryAfter!.Delta!.Value.TotalSeconds, 1, 60);
            Assert.Contains("3 requests a minute", await limited.Content.ReadAsStringAsync());
        }
        finally
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/configuration", With(600))).StatusCode);
            await DeleteKeysAsync(created.ApiKeyKey);
        }
    }
}
