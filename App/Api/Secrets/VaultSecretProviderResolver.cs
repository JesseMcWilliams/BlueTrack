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
                $"'{active.BackendType}' is the active Secrets Storage backend, but no provider implementation exists for it yet.",
                CyberArkErrorCategory.Other);
        }

        return provider;
    }
}
