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
}
