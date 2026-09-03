namespace BlueTrack.Api.Secrets;

/// <summary>
/// The modular provider interface D-16 calls for: the application makes a
/// standard request, and gets back a standardized object, regardless of
/// which of the six Secrets Storage backends is behind it. Only
/// CyberArkCpSecretsProvider exists so far (D-32/D-36) -- Windows DPAPI,
/// the design's actual "first backend built," still has no implementation
/// despite being seeded active by default in web.secrets_store.
/// </summary>
public interface ISecretsProvider
{
    Task<SecretResult> GetSecretAsync(SecretQuery query);
}

/// <summary>
/// Safe/Folder/Object identify which secret to retrieve -- these are
/// per-call parameters, not backend configuration (a Vault can hold many
/// secrets; the active backend's own settings, like CyberArk CP's AppID,
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
