namespace BlueTrack.Api.Secrets;

/// <summary>
/// D-79: split from the original single ISecretsProvider once CyberArk CCP
/// (a second, structurally-similar-but-distinct backend) needed to fit
/// alongside CyberArk CP. Covers every backend that's a remote vault
/// lookup by reference -- CyberArk CP/CCP/Conjur, Azure Key Vault, AWS
/// Secrets Manager -- all "give me the secret named X," even though each
/// vault's own reference shape differs (a Key Vault secret URI isn't a
/// Safe/Folder/Object triple; each provider still takes SecretQuery,
/// interpreting Safe/Folder/Object however makes sense for it). Windows
/// DPAPI does NOT implement this -- see ILocalSecretProtector instead, a
/// deliberately different shape for a deliberately different operation
/// (local encrypt/decrypt, not a vault lookup).
/// </summary>
public interface IVaultSecretProvider
{
    /// <summary>Matches a web.secrets_store.BackendType value (e.g. "CyberArkCP", "CyberArkCCP") -- how VaultSecretProviderResolver picks the active one.</summary>
    string BackendType { get; }

    Task<SecretResult> GetSecretAsync(SecretQuery query);
}

/// <summary>
/// Safe/Folder/Object identify which secret to retrieve -- these are
/// per-call parameters, not backend configuration (a Vault can hold many
/// secrets; each backend's own settings, like CyberArk CP/CCP's AppID,
/// live in web.secrets_store.BackendSettings instead, per D-49).
/// </summary>
public sealed record SecretQuery(string Safe, string Folder, string Object);

/// <summary>
/// Content is char[], not string, per Design_Secrets_Storage.md's own note
/// that a live secret held in application memory "needs the same care as
/// any in-memory credential handling." UserName/Address are non-secret
/// identifying metadata (D-39) -- safe to log or show in an admin UI,
/// unlike Content.
/// </summary>
public sealed record SecretResult(char[] Content, string? UserName, string? Address, bool FromFallbackCache);
