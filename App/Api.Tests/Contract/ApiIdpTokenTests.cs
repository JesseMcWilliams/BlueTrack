using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using BlueTrack.Api.ApiAccess;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-243 (phase 2): a token from an OIDC identity provider exchanged for a BlueTrack token, through the real
/// HTTP pipeline. The identity provider is a fake: its metadata comes from a test key pair, so no network is
/// involved. The OIDC provider row in BlueTrackTest is pointed at it for each test and restored afterwards.
/// </summary>
public class ApiIdpTokenTests : IClassFixture<BlueTrackWebApplicationFactory>, IAsyncLifetime
{
    private const string Authority = "https://idp.test/application/o/bluetrack/";

    private sealed class FakeMetadataSource(IdpMetadata metadata) : IIdpMetadataSource
    {
        public Task<IdpMetadata> GetAsync(string authority, CancellationToken cancellationToken = default) => Task.FromResult(metadata);
    }

    private readonly RSA _idpKey = RSA.Create(2048);
    private readonly RSA _otherKey = RSA.Create(2048);
    private readonly WebApplicationFactory<Program> _factory;
    private readonly List<int> _mappings = [];
    private (int ProviderKey, bool IsEnabled, string? ConfigurationValues) _originalOidc;

    public ApiIdpTokenTests(BlueTrackWebApplicationFactory factory)
    {
        var metadata = new IdpMetadata(Authority, [new RsaSecurityKey(_idpKey.ExportParameters(false)) { KeyId = "idp-key-1" }]);
        _factory = factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IIdpMetadataSource>();
            services.AddSingleton<IIdpMetadataSource>(new FakeMetadataSource(metadata));
        }));
    }

    public async Task InitializeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        _originalOidc = await connection.QuerySingleAsync<(int, bool, string?)>(
            "SELECT TOP (1) ProviderKey, IsEnabled, ConfigurationValues FROM web.identity_provider_config WHERE ProviderType = 'OIDC' ORDER BY ProviderKey");
        await SetOidcAsync(true, $$"""{"authority":"{{Authority}}","clientId":"bluetrack","groupsClaimType":"groups"}""");
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        if (_mappings.Count > 0) await connection.ExecuteAsync("DELETE FROM web.api_client_mapping WHERE ApiClientMappingKey IN @_mappings", new { _mappings });
        await SetOidcAsync(_originalOidc.IsEnabled, _originalOidc.ConfigurationValues);
        _idpKey.Dispose();
        _otherKey.Dispose();
        await _factory.DisposeAsync();
    }

    private async Task SetOidcAsync(bool isEnabled, string? configurationValues)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("UPDATE web.identity_provider_config SET IsEnabled = @isEnabled, ConfigurationValues = @configurationValues WHERE ProviderKey = @key",
            new { isEnabled, configurationValues, key = _originalOidc.ProviderKey });
    }

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");
        return client;
    }

    private HttpClient ScriptClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Remove(BlueTrack.Api.Security.RequireApiRequestHeaderMiddleware.HeaderName);
        return client;
    }

    private static async Task<int> RoleKeyAsync(string roleName)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<int>("SELECT AppRoleKey FROM web.app_role WHERE RoleName = @roleName", new { roleName });
    }

    private async Task<int> CreateMappingAsync(HttpClient admin, string matchType, string value, string roleName, int? order = null)
    {
        var response = await admin.PostAsJsonAsync("/api/admin/api-client-mappings",
            new { providerKey = _originalOidc.ProviderKey, matchType, matchValue = value, appRoleKey = await RoleKeyAsync(roleName), displayOrder = order });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var key = (await response.Content.ReadFromJsonAsync<JsonObject>())!["apiClientMappingKey"]!.GetValue<int>();
        _mappings.Add(key);
        return key;
    }

    /// <summary>A token as an identity provider would issue it, signed with the fake provider's key unless told otherwise.</summary>
    private string IdpToken(string subject = "svc-script", string? clientId = "client-abc", string[]? groups = null, string issuer = Authority,
        DateTime? expires = null, RSA? signWith = null, bool symmetric = false)
    {
        var claims = new Dictionary<string, object> { ["sub"] = subject, ["preferred_username"] = subject };
        if (clientId is not null) claims["azp"] = clientId;
        if (groups is not null) claims["groups"] = groups;
        var credentials = symmetric
            ? new SigningCredentials(new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)), SecurityAlgorithms.HmacSha256)
            : new SigningCredentials(new RsaSecurityKey(signWith ?? _idpKey) { KeyId = "idp-key-1" }, SecurityAlgorithms.RsaSha256);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = "some-other-app",
            NotBefore = (expires ?? DateTime.UtcNow.AddMinutes(5)).AddMinutes(-10),
            Expires = expires ?? DateTime.UtcNow.AddMinutes(5),
            Claims = claims,
            SigningCredentials = credentials,
        });
    }

    private static async Task<HttpResponseMessage> ExchangeAsync(HttpClient client, string idToken) =>
        await client.PostAsJsonAsync("/api/auth/token", new { idToken });

    private static HttpRequestMessage Get(string url, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static async Task<string> AccessTokenAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(600, body["expires_in"]!.GetValue<int>());
        return body["access_token"]!.GetValue<string>();
    }

    [Fact]
    public async Task ClientIdMapping_GivesTheMappedRole_NotAdminAndNotTheKeyManagementPages()
    {
        var admin = AdminClient();
        await CreateMappingAsync(admin, "ClientId", "client-abc", "Viewer");
        var script = ScriptClient();

        var token = await AccessTokenAsync(await ExchangeAsync(script, IdpToken()));

        var me = await script.SendAsync(Get("/api/me", token));
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var body = (await me.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(["Viewer"], body["roleNames"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal("API client: svc-script", body["displayName"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Forbidden, (await script.SendAsync(Get("/api/admin/api-keys", token))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await script.SendAsync(Get("/api/admin/api-client-mappings", token))).StatusCode);

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var details = (await connection.QueryAsync<string>("""
            SELECT TOP (5) e.Detail FROM web.audit_event e
            JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey
            WHERE t.EventTypeName = 'ApiTokenIssued' ORDER BY e.AuditEventKey DESC
            """)).ToList();
        Assert.Contains(details, d => d.Contains("svc-script") && d.Contains("ClientId client-abc") && d.Contains("role Viewer"));
    }

    [Fact]
    public async Task GroupMapping_Matches_AndTheLowestOrderWinsOverAClientIdMatch()
    {
        var admin = AdminClient();
        await CreateMappingAsync(admin, "ClientId", "client-abc", "Viewer", order: 50);
        await CreateMappingAsync(admin, "Group", "bt-auditors", "Auditor", order: 10);
        var script = ScriptClient();

        // The group mapping has the lower order, so it wins even though the client matches too.
        var both = await AccessTokenAsync(await ExchangeAsync(script, IdpToken(groups: ["other", "bt-auditors"])));
        var asBoth = (await (await script.SendAsync(Get("/api/me", both))).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(["Auditor"], asBoth["roleNames"]!.AsArray().Select(r => r!.GetValue<string>()));

        // Without the group, the client mapping applies.
        var clientOnly = await AccessTokenAsync(await ExchangeAsync(script, IdpToken(groups: ["other"])));
        var asClient = (await (await script.SendAsync(Get("/api/me", clientOnly))).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal(["Viewer"], asClient["roleNames"]!.AsArray().Select(r => r!.GetValue<string>()));

        // A token with only the group (no client id at all) works too.
        var groupOnly = await AccessTokenAsync(await ExchangeAsync(script, IdpToken(subject: "a.person", clientId: null, groups: ["bt-auditors"])));
        var asPerson = (await (await script.SendAsync(Get("/api/me", groupOnly))).Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Equal("API client: a.person", asPerson["displayName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Token_NeverCarriesThePermissionsAnApiCallerCanNotHave()
    {
        var admin = AdminClient();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var roleName = $"CT idp role {Guid.NewGuid():N}"[..22];
        var roleKey = await connection.QuerySingleAsync<int>("INSERT INTO web.app_role (RoleName) OUTPUT inserted.AppRoleKey VALUES (@roleName)", new { roleName });
        await connection.ExecuteAsync("""
            INSERT INTO web.role_permission (RoleKey, PermissionKey)
            SELECT @roleKey, PermissionKey FROM web.app_permission WHERE PermissionName IN ('ViewDashboard', 'ManageApiKeys', 'ManageSecretsStore', 'ReloadRights')
            """, new { roleKey });
        try
        {
            await CreateMappingAsync(admin, "ClientId", "client-perm", roleName);
            var script = ScriptClient();
            var token = await AccessTokenAsync(await ExchangeAsync(script, IdpToken(clientId: "client-perm")));
            var me = (await (await script.SendAsync(Get("/api/me", token))).Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Equal(["ViewDashboard"], me["permissionNames"]!.AsArray().Select(p => p!.GetValue<string>()));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.api_client_mapping WHERE AppRoleKey = @roleKey", new { roleKey });
            await connection.ExecuteAsync("DELETE FROM web.role_permission WHERE RoleKey = @roleKey; DELETE FROM web.app_role WHERE AppRoleKey = @roleKey", new { roleKey });
        }
    }

    [Fact]
    public async Task Refusals_AreAll401_AndNeverFallBackToAnotherSignIn()
    {
        var admin = AdminClient();
        await CreateMappingAsync(admin, "ClientId", "client-abc", "Viewer");
        var script = ScriptClient();

        // Unknown client (no mapping); another issuer; an expired token; signed by someone else's key; a symmetric
        // signature (forgeable); not a token at all.
        var refused = new[]
        {
            IdpToken(clientId: "client-unmapped"),
            IdpToken(issuer: "https://evil.example/"),
            IdpToken(expires: DateTime.UtcNow.AddMinutes(-30)),
            IdpToken(signWith: _otherKey),
            IdpToken(symmetric: true),
            "not.a.token",
        };
        foreach (var token in refused)
        {
            var response = await ExchangeAsync(script, token);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.DoesNotContain("client-abc", await response.Content.ReadAsStringAsync());
        }

        // An identity provider that is switched off is not trusted.
        await SetOidcAsync(false, null);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ExchangeAsync(script, IdpToken())).StatusCode);
        await SetOidcAsync(true, $$"""{"authority":"{{Authority}}","clientId":"bluetrack","groupsClaimType":"groups"}""");
        await AccessTokenAsync(await ExchangeAsync(script, IdpToken()));

        // Exactly one credential.
        Assert.Equal(HttpStatusCode.BadRequest, (await script.PostAsJsonAsync("/api/auth/token", new { apiKey = "bt_x", idToken = IdpToken() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await script.PostAsJsonAsync("/api/auth/token", new { })).StatusCode);
    }

    [Fact]
    public async Task Disabling_OrDeletingAMapping_StopsNewExchangesAndTheTokensItGave()
    {
        var admin = AdminClient();
        var mapping = await CreateMappingAsync(admin, "ClientId", "client-abc", "Viewer");
        var script = ScriptClient();
        var token = await AccessTokenAsync(await ExchangeAsync(script, IdpToken()));
        Assert.Equal(HttpStatusCode.OK, (await script.SendAsync(Get("/api/me", token))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/admin/api-client-mappings/{mapping}/disable", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await script.SendAsync(Get("/api/me", token))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await ExchangeAsync(script, IdpToken())).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/admin/api-client-mappings/{mapping}/enable", null)).StatusCode);
        var again = await AccessTokenAsync(await ExchangeAsync(script, IdpToken()));
        Assert.Equal(HttpStatusCode.OK, (await script.SendAsync(Get("/api/me", again))).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/api-client-mappings/{mapping}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await script.SendAsync(Get("/api/me", again))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync($"/api/admin/api-client-mappings/{mapping}")).StatusCode);
    }

    [Fact]
    public async Task MappingManagement_ValidatesTheRequest_AndNeedsTheManageApiKeysPermission()
    {
        var admin = AdminClient();
        var viewerRole = await RoleKeyAsync("Viewer");
        var provider = _originalOidc.ProviderKey;
        async Task<HttpStatusCode> Post(object body) => (await admin.PostAsJsonAsync("/api/admin/api-client-mappings", body)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { providerKey = provider, matchType = "ClientId", matchValue = "x", appRoleKey = await RoleKeyAsync("Admin") }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { providerKey = provider, matchType = "ClientId", matchValue = "x", appRoleKey = 0 }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { providerKey = provider, matchType = "Email", matchValue = "x", appRoleKey = viewerRole }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { providerKey = provider, matchType = "Group", matchValue = " ", appRoleKey = viewerRole }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { providerKey = provider, matchType = "Group", matchValue = "x", appRoleKey = viewerRole, displayOrder = 10001 }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { providerKey = 0, matchType = "Group", matchValue = "x", appRoleKey = viewerRole }));

        await CreateMappingAsync(admin, "Group", "dup-group", "Viewer");
        Assert.Equal(HttpStatusCode.Conflict, await Post(new { providerKey = provider, matchType = "group", matchValue = "dup-group", appRoleKey = viewerRole }));

        var options = (await admin.GetFromJsonAsync<JsonObject>("/api/admin/api-client-mappings/options"))!;
        Assert.Contains(provider, options["providers"]!.AsArray().Select(p => p!["providerKey"]!.GetValue<int>()));
        Assert.DoesNotContain("Admin", options["roles"]!.AsArray().Select(r => r!["roleName"]!.GetValue<string>()));
        Assert.Equal(["ClientId", "Group"], options["matchTypes"]!.AsArray().Select(t => t!.GetValue<string>()));

        var viewer = _factory.CreateClient();
        viewer.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/admin/api-client-mappings")).StatusCode);
    }
}
