namespace BlueTrack.Api.Models;

/// <summary>
/// web.identity_provider_config.ConfigurationValues shape for ProviderType
/// "OIDC" (D-84). No real IdP metadata exists yet -- see D-25's requirement
/// that it be pulled once and embedded as local config, never fetched
/// dynamically, so these are the values an admin fills in once real tenant
/// details arrive, not something this app resolves on its own.
/// </summary>
public sealed class OidcProviderSettings
{
    /// <summary>The IdP's issuer/authority URL, e.g. https://login.microsoftonline.com/{tenant}/v2.0.</summary>
    public string Authority { get; init; } = "";

    public string ClientId { get; init; } = "";

    /// <summary>ASP.NET Core's OIDC callback path. Defaults to the framework's own default; only change if the IdP requires a specific redirect URI path.</summary>
    public string CallbackPath { get; init; } = "/signin-oidc";

    /// <summary>
    /// Which claim in the ID token/userinfo response carries group
    /// membership -- varies by IdP (e.g. Entra ID's "groups" claim), so
    /// this is configured rather than assumed (GroupIdentifierExtractor's
    /// own comment explains why).
    /// </summary>
    public string GroupsClaimType { get; init; } = "groups";
}
