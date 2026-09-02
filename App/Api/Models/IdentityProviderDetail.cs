namespace BlueTrack.Api.Models;

/// <summary>
/// Full web.identity_provider_config row for the Identity Providers admin
/// page -- unlike IdentityProviderConfig (used for lightweight login-flow
/// lookups), this carries ConfigurationValues/SecretReference too.
/// </summary>
public sealed class IdentityProviderDetail
{
    public int ProviderKey { get; init; }
    public required string ProviderType { get; init; }
    public required string DisplayName { get; init; }
    public bool IsEnabled { get; init; }
    public int DisplayOrder { get; init; }
    public string? ConfigurationValues { get; init; }
    public string? SecretReference { get; init; }
}

public sealed class SaveIdentityProviderRequest
{
    public required string ProviderType { get; init; }
    public required string DisplayName { get; init; }
    public bool IsEnabled { get; init; }
    public int DisplayOrder { get; init; }
    public string? ConfigurationValues { get; init; }
    public string? SecretReference { get; init; }
}
