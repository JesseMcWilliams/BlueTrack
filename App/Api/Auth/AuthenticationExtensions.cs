using Microsoft.AspNetCore.Authentication.Negotiate;

namespace BlueTrack.Api.Auth;

/// <summary>
/// Wires up the modular provider set from Design_Authentication_Architecture.md.
/// Windows Integrated is registered unconditionally -- it needs no external
/// config (D-30 already puts the app itself on Windows Integrated auth to
/// SQL Server, so the app server is domain-joined regardless).
///
/// OIDC and SAML are NOT wired here yet: D-25 is explicit that IdP metadata
/// and signing certificates must be pulled once and embedded as local
/// config/files, never fetched dynamically at runtime -- and no real IdP
/// metadata exists for this environment yet. Wiring them with placeholder
/// values would be worse than not wiring them at all. Add
/// AddOpenIdConnect(...) / the ITfoxtec.Identity.Saml2 setup here once real
/// tenant/metadata values are available, following the SAML Security
/// Hardening section of that same document (D-25, D-26, D-27).
///
/// DevFakeAuth (Development-only, guarded in code per the design doc's own
/// "Guard condition" -- not just an admin toggle) is not implemented here
/// yet either -- it needs the same identity_group_role_map-backed claims
/// substitution the real providers use, which depends on
/// Design_Authorization_Model.md's tables actually existing and being
/// queryable, not just designed.
/// </summary>
public static class AuthenticationExtensions
{
    public static IServiceCollection AddBlueTrackAuthentication(this IServiceCollection services)
    {
        services
            .AddAuthentication(NegotiateDefaults.AuthenticationScheme)
            .AddNegotiate();

        services.AddAuthorizationBuilder();

        return services;
    }
}
