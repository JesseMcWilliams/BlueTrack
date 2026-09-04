using BlueTrack.Api.Tests.Integration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// AuthenticationSchemeProvider caches its own request-handler-scheme list
/// independent of later AuthenticationOptions.SchemeMap mutations (confirmed
/// directly, 2026-09-04: removing "Negotiate" from SchemeMap via a later
/// Configure&lt;AuthenticationOptions&gt; call left GetRequestHandlerSchemesAsync()
/// still returning it) -- overriding this one virtual method is the
/// reliable way to stop AuthenticationMiddleware from ever invoking
/// NegotiateHandler.HandleRequestAsync() against TestServer.
/// </summary>
file sealed class NegotiateFreeSchemeProvider(IOptions<AuthenticationOptions> options) : AuthenticationSchemeProvider(options)
{
    public override Task<IEnumerable<AuthenticationScheme>> GetRequestHandlerSchemesAsync() =>
        Task.FromResult(Enumerable.Empty<AuthenticationScheme>());
}

/// <summary>
/// Layer 3 (Design_Testing_Strategy.md): hosts the real BlueTrack.Api
/// Program in-process over real HTTP (WebApplicationFactory), backed by
/// BlueTrackTest (see TestDatabase), authenticating via TestAuthHandler
/// instead of a real Negotiate handshake.
///
/// Overrides AuthenticationOptions' Default(Authenticate/Challenge)Scheme
/// to TestAuthHandler.SchemeName -- registered after Program.cs's own
/// AddBlueTrackAuthentication/the post-AddSaml2() Negotiate-forcing
/// Configure&lt;AuthenticationOptions&gt; call, so this one wins (later
/// Configure&lt;T&gt; registrations run after earlier ones against the same
/// options instance). Every other real middleware -- PermissionClaimsTransformation,
/// the authorization policies, controllers -- runs completely unchanged.
///
/// Also registers NegotiateFreeSchemeProvider (above), not just the
/// Default* pointers: Negotiate's handler implements
/// IAuthenticationRequestHandler, which ASP.NET Core's AuthenticationMiddleware
/// invokes for every request-handler scheme on every request, regardless of
/// DefaultScheme -- discovered directly (2026-09-04) via TestServer, which
/// has no Kestrel connection to hand it, throwing
/// "Negotiate authentication requires a server that supports
/// IConnectionItemsFeature like Kestrel" on literally every request until
/// this was added.
/// </summary>
public sealed class BlueTrackWebApplicationFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:BlueTrackDb"] = TestDatabase.ConnectionString
            });
        });

        builder.ConfigureServices(services =>
        {
            services
                .AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

            services.Configure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = TestAuthHandler.SchemeName;
                options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
            });

            services.AddSingleton<IAuthenticationSchemeProvider, NegotiateFreeSchemeProvider>();
        });
    }
}
