using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using BlueTrack.Api.Security;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-197, from the 2026-10-10 security review: reading tracked data needs
/// ViewDashboard (not just a sign-in), state-changing calls need the
/// X-Requested-With header (CSRF), and sign-in only redirects to local paths.
/// </summary>
public class SecurityHardeningTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public SecurityHardeningTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient ClientAs(string username, bool withRequestHeader = true)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, username);
        if (!withRequestHeader)
        {
            client.DefaultRequestHeaders.Remove(RequireApiRequestHeaderMiddleware.HeaderName);
        }
        return client;
    }

    public static TheoryData<string> ReadEndpoints => new()
    {
        "/api/account-progress",
        "/api/account-progress/field-metadata",
        "/api/reports/kpi-summary",
        "/api/reports/stage-status-summary",
        "/api/reports/decom-accounts",
        "/api/risk-exceptions",
        "/api/risk-exceptions/overdue-review",
        "/api/applications",
    };

    [Theory]
    [MemberData(nameof(ReadEndpoints))]
    public async Task ReadingTrackedData_NeedsViewDashboard(string path)
    {
        // Signed in, but mapped to no role: no permissions at all.
        var unmapped = await ClientAs("TestUser.DoesNotExist").GetAsync(path);
        Assert.Equal(HttpStatusCode.Forbidden, unmapped.StatusCode);

        var viewer = await ClientAs("TestUser.Viewer").GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, viewer.StatusCode);
    }

    [Fact]
    public async Task StateChangingCall_WithoutRequestHeader_IsRefused_ButReadsAreNot()
    {
        var client = ClientAs("TestUser.Admin", withRequestHeader: false);

        var post = await client.PostAsJsonAsync("/api/admin/roles", new { roleName = "", permissionNames = Array.Empty<string>() });
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        var problem = await post.Content.ReadFromJsonAsync<JsonObject>();
        Assert.Equal("Missing request header", problem!["title"]!.GetValue<string>());

        var delete = await client.DeleteAsync("/api/admin/roles/0");
        Assert.Equal(HttpStatusCode.Forbidden, delete.StatusCode);

        var get = await client.GetAsync("/api/admin/roles");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
    }

    [Fact]
    public async Task StateChangingCall_WithRequestHeader_ReachesTheApi()
    {
        // An empty role name is a validation failure from the controller --
        // proof the request got past the header check.
        var response = await ClientAs("TestUser.Admin").PostAsJsonAsync("/api/admin/roles", new { roleName = "", permissionNames = Array.Empty<string>() });
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task SamlAssertionConsumer_IsExemptFromTheHeaderCheck()
    {
        // The identity provider's page posts here as a plain form; whatever the
        // SAML handling then says, it must not be the header check's refusal.
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Remove(RequireApiRequestHeaderMiddleware.HeaderName);
        var response = await client.PostAsync("/api/auth/saml/acs", new FormUrlEncodedContent([new KeyValuePair<string, string>("SAMLResponse", "x")]));
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("Missing request header", body);
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example/")]
    [InlineData("/\\evil.example/")]
    public async Task SignIn_RefusesToRedirectToAnotherSite(string returnUrl)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync($"/api/auth/dev/test-signin?username=TestUser.Viewer&returnUrl={Uri.EscapeDataString(returnUrl)}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SignIn_RedirectsToALocalPath()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/api/auth/dev/test-signin?username=TestUser.Viewer&returnUrl=%2Faccounts");
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/accounts", response.Headers.Location!.OriginalString);
    }
}
