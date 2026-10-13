using System.Net;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-209: a cookie session (the development-only test sign-in, same scheme as OIDC)
/// is refused once it has been idle longer than Idle Timeout, and the SAML session
/// scheme gets the same check.
/// </summary>
public class IdleSessionTests : IClassFixture<IdleSessionTests.ClockFactory>
{
    public sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>The normal test host with the clock under the test's control.</summary>
    public sealed class ClockFactory : WebApplicationFactory<Program>
    {
        public ManualClock Clock { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:BlueTrackDb", Integration.TestDatabase.ConnectionString);
            // As App/E2E runs it: no Negotiate (TestServer can't host it); cookie sessions only.
            builder.UseSetting("BlueTrack:DisableNegotiate", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
            });
        }
    }

    private readonly ClockFactory _factory;

    public IdleSessionTests(ClockFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task ACookieSession_IsRefusedAfterIdleTimeout_AndKeptAliveByUse()
    {
        int idleMinutes;
        using (var scope = _factory.Services.CreateScope())
        {
            idleMinutes = (await scope.ServiceProvider.GetRequiredService<AppConfigRepository>().GetAsync()).IdleTimeoutMinutes;
        }
        IdleSessionValidator.ForgetCachedSetting();

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = true });
        var signIn = await client.GetAsync("/api/auth/dev/test-signin?username=TestUser.Viewer");
        Assert.True(signIn.IsSuccessStatusCode, $"test sign-in: {(int)signIn.StatusCode} {await signIn.Content.ReadAsStringAsync()}");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);

        // Used within the window: still signed in, and the window starts again.
        _factory.Clock.Now += TimeSpan.FromMinutes(idleMinutes - 1);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);
        _factory.Clock.Now += TimeSpan.FromMinutes(idleMinutes - 1);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/me")).StatusCode);

        // Idle longer than the timeout: refused.
        _factory.Clock.Now += TimeSpan.FromMinutes(idleMinutes + 1);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me")).StatusCode);
    }

    [Fact]
    public async Task TheSamlSessionScheme_IsACookieScheme_WithTheIdleCheck()
    {
        var schemes = _factory.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        var saml = await schemes.GetSchemeAsync(ITfoxtec.Identity.Saml2.Schemas.Saml2Constants.AuthenticationScheme);
        Assert.NotNull(saml);
        Assert.Equal(typeof(CookieAuthenticationHandler), saml!.HandlerType);

        var options = _factory.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(ITfoxtec.Identity.Saml2.Schemas.Saml2Constants.AuthenticationScheme);
        Assert.NotNull(options.Events.OnValidatePrincipal);
        Assert.Contains(nameof(IdleSessionValidator), options.Events.OnValidatePrincipal.Method.DeclaringType?.FullName ?? "");
    }
}
