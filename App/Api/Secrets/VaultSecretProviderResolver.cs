using BlueTrack.Api.Data;

namespace BlueTrack.Api.Secrets;

/// <summary>
/// D-16's "unified... modular provider interface" made real now that more
/// than one IVaultSecretProvider exists (CyberArk CP and CCP, D-79/D-80):
/// picks whichever one matches web.secrets_store's currently-active
/// BackendType. Previously the only implementation was injected directly
/// wherever a secret was needed; that stopped being safe to assume once a
/// second one existed.
/// </summary>
public sealed class VaultSecretProviderResolver(
    IEnumerable<IVaultSecretProvider> providers,
    SecretsStoreRepository secretsStoreRepository)
{
    public async Task<IVaultSecretProvider> ResolveActiveAsync()
    {
        var backends = await secretsStoreRepository.GetAllAsync();
        var active = backends.FirstOrDefault(b => b.IsActive);
        if (active is null)
        {
            throw new SecretRetrievalException("No active Secrets Storage backend is configured.", CyberArkErrorCategory.Other);
        }

        var provider = providers.FirstOrDefault(p => p.BackendType == active.BackendType);
        if (provider is null)
        {
            throw new SecretRetrievalException(
                NotBuiltIn(active.BackendType) ?? $"'{active.BackendType}' is the active Secrets Storage backend, but no provider implementation exists for it yet.",
                CyberArkErrorCategory.Other);
        }

        return provider;
    }

    /// <summary>
    /// D-116: web.credential lets an admin pick a backend per named
    /// credential, independent of web.secrets_store's single "active"
    /// backend (a different concept -- see that table's own comment).
    /// Resolves whichever registered IVaultSecretProvider matches, with no
    /// "is it the active one" check at all.
    /// </summary>
    public IVaultSecretProvider ResolveByBackendType(string backendType)
    {
        var provider = providers.FirstOrDefault(p => p.BackendType == backendType);
        if (provider is null)
        {
            throw new SecretRetrievalException(
                NotBuiltIn(backendType) ?? $"'{backendType}' has no provider implementation registered.", CyberArkErrorCategory.Other);
        }

        return provider;
    }

    /// <summary>
    /// D-310: the CP provider is compiled in only when the CyberArk Application Password SDK was on the server when the
    /// installer built the API (D-169), so a Credential Provider installed later needs that step run again.
    /// </summary>
    private static string? NotBuiltIn(string backendType) => backendType == "CyberArkCP"
        ? "BlueTrack was built without the CyberArk Application Password SDK, so the CyberArk CP backend isn't available. "
          + "Install the Credential Provider on this server, then re-run the installer's Build.Api step (Install-BlueTrack.ps1 -Step Build.Api)."
        : null;
}
