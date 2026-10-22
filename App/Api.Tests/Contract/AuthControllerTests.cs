using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// Layer 3: the login-flow endpoints that don't need a real external IdP
/// to exercise -- OIDC/SAML with real metadata stay manually verified per
/// Design_Testing-Strategy.md's own "Explicitly not automated" section.
/// </summary>
public class AuthControllerTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public AuthControllerTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetEnabledProviders_Anonymous_ReturnsOnlyEnabledProviders()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<ProvidersResponse>();
        Assert.NotNull(body);
        // WindowsIntegrated (05_BlueTrack_Baseline_WebSeed.sql) and
        // DevFakeAuth (Database/Test/01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql)
        // are both enabled in BlueTrackTest.
        Assert.Contains(body!.Providers, p => p.ProviderType == "WindowsIntegrated");
        Assert.Contains(body.Providers, p => p.ProviderType == "DevFakeAuth");
        // Never exposes DevFakeAuth's admin-only config details -- just the
        // pre-login-screen shape.
        Assert.All(body.Providers, p => Assert.False(string.IsNullOrEmpty(p.DisplayName)));
        // D-156: BLUETRACK_TEST_CONNECTION-backed test runs never set
        // BlueTrack:DisableNegotiate, so this must always read false here --
        // a true value would mean the flag leaked into a context it wasn't
        // meant for.
        Assert.False(body.NegotiateDisabled);
    }

    [Fact]
    public async Task LoginOidc_NotConfigured_ReturnsServiceUnavailable()
    {
        // OIDC is never registered as an auth scheme in BlueTrackTest (no
        // real IdP metadata exists) -- AuthenticationExtensions.cs only
        // registers it when a real-looking, enabled row exists at startup.
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/login/oidc");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Logout_Anonymous_IsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_Authenticated_Succeeds()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Viewer");

        var response = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    private sealed record SessionResponse(bool Authenticated, bool Windows, string? DefaultProviderType);
    private sealed record DefaultBody(string? ProviderType);

    private HttpClient Admin()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");
        return client;
    }

    /// <summary>D-205: never a 401 (a 401 would trigger the browser's silent Windows sign-in).</summary>
    [Fact]
    public async Task Session_Anonymous_Returns200_AndWantsWindowsOnlyWhenItIsTheDefault()
    {
        var admin = Admin();
        var before = (await admin.GetFromJsonAsync<DefaultBody>("/api/admin/identity-providers/default"))!.ProviderType;
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/identity-providers/default", new { providerType = "WindowsIntegrated" })).StatusCode);
            var anonymous = _factory.CreateClient();
            var response = await anonymous.GetAsync("/api/auth/session");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var session = await response.Content.ReadFromJsonAsync<SessionResponse>();
            Assert.False(session!.Authenticated);
            Assert.True(session.Windows);
            Assert.Equal("WindowsIntegrated", session.DefaultProviderType);

            // No default: no Windows attempt unless the user chose it.
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/identity-providers/default", new { providerType = (string?)null })).StatusCode);
            var noDefault = await anonymous.GetFromJsonAsync<SessionResponse>("/api/auth/session");
            Assert.False(noDefault!.Windows);
            Assert.Null(noDefault.DefaultProviderType);
            var providers = await anonymous.GetFromJsonAsync<System.Text.Json.Nodes.JsonObject>("/api/auth/providers");
            Assert.Null(providers!["defaultProviderType"]);

            var chose = new HttpRequestMessage(HttpMethod.Get, "/api/auth/session");
            chose.Headers.Add("Cookie", $"{BlueTrack.Api.Controllers.AuthController.SignInChoiceCookieName}=windows");
            Assert.True((await (await anonymous.SendAsync(chose)).Content.ReadFromJsonAsync<SessionResponse>())!.Windows);
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/admin/identity-providers/default", new { providerType = before });
        }
    }

    [Fact]
    public async Task LoginWindows_RecordsTheChoice_AndRedirectsLocallyOnly()
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/api/auth/login/windows?returnUrl=%2Ftargets");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/targets", response.Headers.Location!.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), c => c.StartsWith(BlueTrack.Api.Controllers.AuthController.SignInChoiceCookieName + "="));
        Assert.Contains("windows", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);

        var offSite = await client.GetAsync("/api/auth/login/windows?returnUrl=https%3A%2F%2Fevil.example%2F");
        Assert.Equal("/", offSite.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task SetDefault_RejectsAnUnknownType_AndRequiresManageIdentityProviders()
    {
        Assert.Equal(HttpStatusCode.BadRequest,
            (await Admin().PutAsJsonAsync("/api/admin/identity-providers/default", new { providerType = "DevFakeAuth" })).StatusCode);

        var viewer = _factory.CreateClient();
        viewer.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Viewer");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.PutAsJsonAsync("/api/admin/identity-providers/default", new { providerType = (string?)null })).StatusCode);
    }

    private sealed record RememberBody(string? ProviderType, bool RememberLastSignInMethod);

    private HttpRequestMessage WithLastSignIn(string path, string providerType)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Cookie", $"{BlueTrack.Api.Controllers.AuthController.LastSignInCookieName}={providerType}");
        return request;
    }

    /// <summary>
    /// D-292: with "remember each browser's last method" on, the sign-in page and the session start the method in
    /// the browser's last-sign-in cookie while it's enabled; otherwise (setting off, or that method not enabled)
    /// the default.
    /// </summary>
    [Fact]
    public async Task RememberLastSignInMethod_StartsTheBrowsersLastMethod_OnlyWhenOnAndEnabled()
    {
        var admin = Admin();
        var before = (await admin.GetFromJsonAsync<RememberBody>("/api/admin/identity-providers/default"))!;
        var anonymous = _factory.CreateClient();
        try
        {
            // No default; the setting off: the cookie is ignored.
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/identity-providers/default",
                new { providerType = (string?)null, rememberLastSignInMethod = false })).StatusCode);
            var off = await (await anonymous.SendAsync(WithLastSignIn("/api/auth/session", "WindowsIntegrated"))).Content.ReadFromJsonAsync<SessionResponse>();
            Assert.Null(off!.DefaultProviderType);
            Assert.False(off.Windows);

            // On: the last method (WindowsIntegrated is enabled in the test database) is started.
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/identity-providers/default",
                new { providerType = (string?)null, rememberLastSignInMethod = true })).StatusCode);
            Assert.True((await admin.GetFromJsonAsync<RememberBody>("/api/admin/identity-providers/default"))!.RememberLastSignInMethod);
            var on = await (await anonymous.SendAsync(WithLastSignIn("/api/auth/session", "WindowsIntegrated"))).Content.ReadFromJsonAsync<SessionResponse>();
            Assert.Equal("WindowsIntegrated", on!.DefaultProviderType);
            Assert.True(on.Windows);
            var providers = await (await anonymous.SendAsync(WithLastSignIn("/api/auth/providers", "WindowsIntegrated"))).Content.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>();
            Assert.Equal("WindowsIntegrated", (string?)providers!["defaultProviderType"]);

            // A method that isn't enabled (SAML is disabled in the test database), or isn't a sign-in method, falls back to the default.
            Assert.Null((await (await anonymous.SendAsync(WithLastSignIn("/api/auth/session", "SAML"))).Content.ReadFromJsonAsync<SessionResponse>())!.DefaultProviderType);
            Assert.Null((await (await anonymous.SendAsync(WithLastSignIn("/api/auth/session", "DevFakeAuth"))).Content.ReadFromJsonAsync<SessionResponse>())!.DefaultProviderType);
            // No cookie: the default.
            Assert.Null((await anonymous.GetFromJsonAsync<SessionResponse>("/api/auth/session"))!.DefaultProviderType);

            // A save without the field (an older page) leaves the setting as it is.
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/identity-providers/default", new { providerType = (string?)null })).StatusCode);
            Assert.True((await admin.GetFromJsonAsync<RememberBody>("/api/admin/identity-providers/default"))!.RememberLastSignInMethod);
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/admin/identity-providers/default",
                new { providerType = before.ProviderType, rememberLastSignInMethod = before.RememberLastSignInMethod });
        }
    }

    /// <summary>D-292: /api/me writes the cookie only for a sign-in method (the test users sign in as DevFakeAuth, which isn't one).</summary>
    [Fact]
    public async Task Me_DoesNotRememberDevFakeAuth()
    {
        var admin = Admin();
        var before = (await admin.GetFromJsonAsync<RememberBody>("/api/admin/identity-providers/default"))!;
        try
        {
            await admin.PutAsJsonAsync("/api/admin/identity-providers/default",
                new { providerType = before.ProviderType, rememberLastSignInMethod = true });
            var response = await admin.GetAsync("/api/me");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain(response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : [],
                c => c.StartsWith(BlueTrack.Api.Controllers.AuthController.LastSignInCookieName + "="));
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/admin/identity-providers/default",
                new { providerType = before.ProviderType, rememberLastSignInMethod = before.RememberLastSignInMethod });
        }
    }

    private sealed class ProviderResponse
    {
        public string ProviderType { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public int DisplayOrder { get; set; }
    }

    private sealed class ProvidersResponse
    {
        public List<ProviderResponse> Providers { get; set; } = [];
        public bool NegotiateDisabled { get; set; }
    }
}
