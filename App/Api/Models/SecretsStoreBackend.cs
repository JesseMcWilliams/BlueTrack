namespace BlueTrack.Api.Models;

/// <summary>One row from web.secrets_store (Design_Secrets_Storage.md).</summary>
public sealed class SecretsStoreBackend
{
    public int SecretStoreKey { get; init; }
    public required string BackendType { get; init; }
    public bool IsActive { get; init; }
    public string? BackendSettings { get; init; }
}

public sealed class SetActiveSecretsStoreRequest
{
    public required string BackendType { get; init; }
    public string? BackendSettings { get; init; }

    /// <summary>
    /// D-84: write-only. When set, the controller protects this via
    /// ILocalSecretProtector and merges it into BackendSettings as
    /// "ProtectedCredential" before saving -- for backends that need a
    /// credential to authenticate to their own remote service (Azure
    /// Key Vault's service principal secret, AWS's secret access key,
    /// Conjur's API key). Never persisted verbatim, never echoed back.
    /// </summary>
    public string? PlaintextCredential { get; init; }
}
