namespace BlueTrack.Api.Models;

public sealed class IdentityProviderConfig
{
    public int ProviderKey { get; init; }
    public required string ProviderType { get; init; } // WindowsIntegrated / OIDC / SAML / DevFakeAuth
    public required string DisplayName { get; init; }
    public bool IsEnabled { get; init; }
    public int DisplayOrder { get; init; }

    /// <summary>
    /// D-84: needed by UserRightsResolver for OIDC/SAML to know which claim
    /// type carries group membership (OidcProviderSettings.GroupsClaimType /
    /// SamlProviderSettings.GroupClaimType) -- WindowsIntegrated/DevFakeAuth
    /// don't use this at all (groups come off the Windows token directly).
    /// </summary>
    public string? ConfigurationValues { get; init; }
}
