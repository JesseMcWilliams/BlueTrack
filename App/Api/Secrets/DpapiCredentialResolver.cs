using Dapper;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Secrets;

/// <summary>
/// D-118: the actual DPAPI decrypt + Machine-to-User scope-upgrade mechanism
/// for a web.credential row, extracted out of CredentialRepository so
/// WindowsDpapiVaultSecretsProvider (an IVaultSecretProvider) can use it too
/// without depending on CredentialRepository itself -- that would create a
/// circular DI dependency (CredentialRepository -> VaultSecretProviderResolver
/// -> IEnumerable&lt;IVaultSecretProvider&gt; -> WindowsDpapiVaultSecretsProvider
/// -> CredentialRepository). Both CredentialRepository (resolving a specific
/// credential by key, the normal SMTP/LDAP path) and
/// WindowsDpapiVaultSecretsProvider (resolving one by name, the
/// web.secrets_store vault-lookup path) delegate here instead.
/// </summary>
public sealed class DpapiCredentialResolver(IDbConnectionFactory connectionFactory, ILocalSecretProtector localSecretProtector)
{
    private sealed class CredentialRow
    {
        public int CredentialKey { get; init; }
        public required string CredentialName { get; init; }
        public string? Username { get; init; }
        public string? ProtectedPassword { get; init; }
        public string? ScopePreference { get; init; }
        public string? CurrentScope { get; init; }
    }

    /// <summary>
    /// See CredentialRepository's own D-116 comment for the full Machine-then-
    /// User upgrade rationale -- unchanged here, just relocated.
    /// </summary>
    public async Task<(string? Username, string Password)> ResolveAsync(int credentialKey)
    {
        using var connection = connectionFactory.Create();
        var row = await connection.QuerySingleAsync<CredentialRow>(
            "SELECT CredentialKey, CredentialName, Username, ProtectedPassword, ScopePreference, CurrentScope FROM web.credential WHERE CredentialKey = @CredentialKey",
            new { CredentialKey = credentialKey });

        if (string.IsNullOrEmpty(row.ProtectedPassword))
        {
            throw new InvalidOperationException($"Credential '{row.CredentialName}' has no password stored yet.");
        }

        var currentScope = ParseScope(row.CurrentScope) ?? CredentialScope.Machine;
        var password = localSecretProtector.Unprotect(row.ProtectedPassword, currentScope);

        if (row.ScopePreference == nameof(CredentialScope.User) && currentScope == CredentialScope.Machine)
        {
            try
            {
                var upgraded = localSecretProtector.Protect(password, CredentialScope.User);
                await connection.ExecuteAsync(
                    "UPDATE web.credential SET ProtectedPassword = @Upgraded, CurrentScope = @CurrentScope WHERE CredentialKey = @CredentialKey",
                    new { Upgraded = upgraded, CurrentScope = nameof(CredentialScope.User), CredentialKey = credentialKey });
            }
            catch
            {
                // Best-effort -- stays at Machine scope, retried on the next resolve.
            }
        }

        return (row.Username, password);
    }

    /// <summary>
    /// D-118: WindowsDpapiVaultSecretsProvider's own entry point -- a
    /// SecretQuery's Object field names a web.credential row directly
    /// (Safe/Folder are accepted but ignored, since a local DPAPI store has
    /// no hierarchy to place them in, unlike a real vault's Safe/Folder).
    /// </summary>
    public async Task<(string? Username, string Password)> ResolveByNameAsync(string credentialName)
    {
        using var connection = connectionFactory.Create();
        var credentialKey = await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT CredentialKey FROM web.credential WHERE CredentialName = @CredentialName AND BackendType = 'WindowsDpapi'",
            new { CredentialName = credentialName });

        if (credentialKey is null)
        {
            throw new SecretRetrievalException($"No WindowsDpapi credential named '{credentialName}' exists.", CyberArkErrorCategory.NotFound);
        }

        return await ResolveAsync(credentialKey.Value);
    }

    private static CredentialScope? ParseScope(string? scope) =>
        Enum.TryParse<CredentialScope>(scope, out var parsed) ? parsed : null;
}
