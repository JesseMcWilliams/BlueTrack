namespace BlueTrack.Api.Models;

public sealed class IdentityProviderConfig
{
    public int ProviderKey { get; init; }
    public required string ProviderType { get; init; } // WindowsIntegrated / OIDC / SAML / DevFakeAuth
    public required string DisplayName { get; init; }
    public bool IsEnabled { get; init; }
    public int DisplayOrder { get; init; }
}
