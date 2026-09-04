using System.Security.Claims;
using Dapper;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Data.SqlClient;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using BlueTrack.Api.Secrets;

namespace BlueTrack.Api.Auth;

/// <summary>
/// Wires up the modular provider set from Design_Authentication_Architecture.md.
/// Windows Integrated is registered unconditionally -- it needs no external
/// config (D-30 already puts the app itself on Windows Integrated auth to
/// SQL Server, so the app server is domain-joined regardless).
///
/// OIDC (D-84): ASP.NET Core registers authentication schemes once at
/// startup, before the DI container exists to serve a scoped
/// VaultSecretProviderResolver -- so this reads web.identity_provider_config
/// directly (a raw SqlConnection, not through DI) at startup, once, and only
/// registers the OIDC scheme at all if a real-looking row exists
/// (IsEnabled, non-empty Authority/ClientId). If the row is absent, disabled,
/// incomplete, or the database isn't reachable/migrated yet, OIDC is simply
/// not registered -- the app behaves exactly as it did before D-84
/// (Negotiate only). This means enabling OIDC via the admin UI requires an
/// app restart to take effect; that's an accepted, documented limitation
/// (see D-84 in the Decision Register), not an oversight -- dynamically
/// adding/removing auth schemes at runtime is real complexity this
/// placeholder framework doesn't need yet.
///
/// The ClientSecret is stored in identity_provider_config.SecretReference,
/// DPAPI-protected (WindowsDpapiProtector, constructed directly here rather
/// than via DI for the same startup-timing reason) -- NOT resolved through
/// the active Secrets Storage backend (VaultSecretProviderResolver), even
/// though that column's own original doc comment describes it as "a
/// pointer into whichever Secrets Storage backend is active." That vault
/// lookup is async and DI-scoped; auth scheme registration needs its
/// ClientSecret synchronously, before the container exists. Same
/// DPAPI-protected-inline convention as the three new Secrets Storage
/// backends (D-84) -- a provider bootstrapping its own credential can't
/// recursively depend on "the active secrets store."
///
/// Negotiate stays the literal DefaultScheme/DefaultChallengeScheme,
/// completely unchanged from before D-84 -- an earlier version of this
/// tried making a policy scheme (AddPolicyScheme, forwarding to Negotiate
/// or Cookies based on which cookie was present) the default instead, and
/// that broke Windows Integrated auth entirely (confirmed by testing:
/// /api/me started returning a bare 401 instead of the Kerberos/NTLM
/// challenge round trip succeeding). Kestrel's built-in Negotiate
/// integration depends on Negotiate actually being the registered default
/// scheme, not sitting behind a policy scheme. Instead, a small middleware
/// in Program.cs (registered right after UseAuthentication) checks for the
/// OIDC-issued cookie and authenticates against the Cookie scheme itself
/// only when Negotiate didn't already authenticate the request -- Negotiate's
/// own challenge behavior for unauthenticated requests is entirely
/// untouched.
///
/// SAML is wired separately (see Saml2Controller/Saml2ConfigurationFactory)
/// since ITfoxtec.Identity.Saml2 isn't an AddAuthentication()-scheme-based
/// library -- it's a set of MVC action helpers the app's own controller
/// calls directly, following the D-25/D-26/D-27 SAML Security Hardening
/// requirements in Design_Authentication_Architecture.md.
///
/// DevFakeAuth (Development-only, guarded in code, not just an admin
/// toggle) is wired via NegotiateProviderResolver -- it shares the
/// Negotiate scheme rather than being a separate one.
/// </summary>
public static class AuthenticationExtensions
{
    public const string OidcAuthCookieName = "BlueTrack.OidcAuth";

    public static IServiceCollection AddBlueTrackAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var oidcSettings = TryLoadOidcStartupSettings(configuration);

        var authBuilder = services
            .AddAuthentication(NegotiateDefaults.AuthenticationScheme)
            .AddNegotiate()
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.Name = OidcAuthCookieName;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.ExpireTimeSpan = TimeSpan.FromHours(8);
            });

        if (oidcSettings is not null)
        {
            authBuilder.AddOpenIdConnect("OIDC", options =>
            {
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.Authority = oidcSettings.Authority;
                options.ClientId = oidcSettings.ClientId;
                options.ClientSecret = oidcSettings.ClientSecret;
                options.CallbackPath = oidcSettings.CallbackPath;
                options.ResponseType = OpenIdConnectResponseType.Code;
                options.SaveTokens = true;
                options.GetClaimsFromUserInfoEndpoint = true;
                options.Events.OnTokenValidated = context =>
                {
                    // Marks this principal as OIDC-authenticated (D-84) --
                    // AuthenticatedProviderResolver (NegotiateProviderResolver.cs)
                    // has no other reliable way to distinguish it once more
                    // than one non-Negotiate scheme can be registered.
                    (context.Principal!.Identity as ClaimsIdentity)?.AddClaim(new Claim(BlueTrackClaimTypes.ProviderType, "OIDC"));
                    return Task.CompletedTask;
                };
            });
        }

        return services;
    }

    private sealed record OidcStartupSettings(string Authority, string ClientId, string ClientSecret, string CallbackPath);

    private static OidcStartupSettings? TryLoadOidcStartupSettings(IConfiguration configuration)
    {
        try
        {
            var connectionString = configuration.GetConnectionString("BlueTrackDb");
            if (string.IsNullOrEmpty(connectionString))
            {
                return null;
            }

            using var connection = new SqlConnection(connectionString);
            var row = connection.QuerySingleOrDefault<(string? ConfigurationValues, string? SecretReference)>("""
                SELECT ConfigurationValues, SecretReference
                FROM web.identity_provider_config
                WHERE ProviderType = 'OIDC' AND IsEnabled = 1
                """);

            if (row.ConfigurationValues is null)
            {
                return null;
            }

            var settings = ProviderSettingsReader.ReadOidc(row.ConfigurationValues);
            if (settings is null || string.IsNullOrWhiteSpace(settings.Authority) || string.IsNullOrWhiteSpace(settings.ClientId))
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(row.SecretReference))
            {
                Console.WriteLine("BlueTrack: OIDC provider is enabled with Authority/ClientId but has no stored client secret (SecretReference) -- not registering the OIDC scheme.");
                return null;
            }

            var clientSecret = new WindowsDpapiProtector().Unprotect(row.SecretReference);
            return new OidcStartupSettings(settings.Authority, settings.ClientId, clientSecret, settings.CallbackPath);
        }
        catch (Exception ex)
        {
            // A fresh/un-migrated database, an unreachable SQL Server at
            // startup, or a client secret protected on a different machine
            // are all real possibilities this early in the app's
            // lifecycle -- fail soft (no OIDC scheme registered, exactly
            // today's Negotiate-only behavior) rather than crash startup.
            Console.WriteLine($"BlueTrack: could not load OIDC provider settings at startup -- OIDC will not be registered this run. {ex.Message}");
            return null;
        }
    }
}
