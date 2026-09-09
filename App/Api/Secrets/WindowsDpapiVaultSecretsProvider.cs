namespace BlueTrack.Api.Secrets;

/// <summary>
/// D-118: makes WindowsDpapi usable as a real web.secrets_store backend --
/// requested directly after the Secrets Store health check was found
/// reporting Unhealthy whenever WindowsDpapi was the active backend, since
/// no IVaultSecretProvider had ever been registered for it (D-79's original
/// split deliberately kept DPAPI as a local encrypt/decrypt primitive,
/// ILocalSecretProtector, not a vault lookup -- see that interface's own
/// comment). This is a second, different code path from that one: a thin
/// adapter over the same web.credential store D-116 already built for the
/// SMTP/LDAP credentials, so an environment with no CyberArk/Azure/AWS
/// access can still use "Windows DPAPI" as its Secrets Store backend for
/// privileged-account-style lookups, sourced locally instead of from an
/// external vault.
///
/// SecretQuery's Object field names a web.credential row (BackendType =
/// WindowsDpapi) directly by CredentialName; Safe/Folder are accepted (so
/// every backend still takes the same three-field query shape) but ignored
/// -- a local DPAPI store is a flat namespace, not a hierarchical vault.
/// </summary>
public sealed class WindowsDpapiVaultSecretsProvider(DpapiCredentialResolver dpapiCredentialResolver) : IVaultSecretProvider
{
    public string BackendType => "WindowsDpapi";

    public async Task<SecretResult> GetSecretAsync(SecretQuery query)
    {
        var (username, password) = await dpapiCredentialResolver.ResolveByNameAsync(query.Object);
        return new SecretResult(password.ToCharArray(), username, Address: "local (Windows DPAPI)", FromFallbackCache: false);
    }
}
