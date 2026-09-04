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

    /// <summary>Deprecated direct-write path -- prefer PlaintextSecret. Kept for provider types that genuinely do store a non-secret reference here rather than a credential.</summary>
    public string? SecretReference { get; init; }

    /// <summary>
    /// D-84: write-only. When set, the repository DPAPI-protects this and
    /// stores it as SecretReference (used today by OIDC's ClientSecret --
    /// see AuthenticationExtensions.cs's own comment on why this column
    /// holds a DPAPI blob rather than a vault-lookup reference for OIDC
    /// specifically). Never persisted verbatim, never echoed back.
    /// </summary>
    public string? PlaintextSecret { get; init; }
}
