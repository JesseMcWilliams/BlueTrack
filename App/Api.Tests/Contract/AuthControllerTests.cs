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
        // WindowsIntegrated (09_BlueTrack_WebSeed.sql) and
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
