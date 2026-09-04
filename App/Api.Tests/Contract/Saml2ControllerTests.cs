using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// Layer 3: SAML is a D-84 placeholder framework -- no SAML row exists in
/// BlueTrackTest, so Saml2ConfigurationFactory.BuildAsync() returns null
/// and every action correctly hits its own NotConfigured() 503, the same
/// pattern already proven for OIDC in AuthControllerTests. Validating a
/// real signed SAML assertion in Acs stays out of scope per
/// Design_Testing_Strategy.md's "Explicitly not automated" section --
/// nothing here reaches a real IdP.
/// </summary>
public class Saml2ControllerTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public Saml2ControllerTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Login_NotConfigured_ReturnsServiceUnavailable()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/saml/login");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Acs_NotConfigured_ReturnsServiceUnavailable()
    {
        // The config check runs before any SAML response is parsed, so a
        // bare POST with no body still reaches NotConfigured() cleanly.
        var client = _factory.CreateClient();

        var response = await client.PostAsync("/api/auth/saml/acs", null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Metadata_NotConfigured_ReturnsServiceUnavailable()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/saml/metadata");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Logout_Anonymous_IsUnauthorized()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/auth/saml/logout");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_Authenticated_Redirects()
    {
        // Logout doesn't check configuration at all (it just signs out of
        // its own scheme regardless), so this succeeds even with no SAML
        // row -- confirmed by reading Saml2Controller.Logout directly.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Viewer");

        var response = await client.GetAsync("/api/auth/saml/logout");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/", response.Headers.Location?.ToString());
    }
}
