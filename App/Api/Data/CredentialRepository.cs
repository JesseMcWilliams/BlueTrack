using Dapper;
using BlueTrack.Api.Models;
using BlueTrack.Api.Secrets;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the new Credentials admin page (D-116) -- a named credential this
/// app authenticates *as* (the SMTP account, the LDAP bind account),
/// independent of web.secrets_store's single active backend for resolving
/// *privileged-account* secrets a vault manages (see that repository's own
/// comment). Each credential picks its own backend; ResolveForUseAsync
/// dispatches to Windows DPAPI locally or to the matching IVaultSecretProvider
/// by BackendType.
/// </summary>
public sealed class CredentialRepository(
    IDbConnectionFactory connectionFactory,
    ILocalSecretProtector localSecretProtector,
    VaultSecretProviderResolver vaultSecretProviderResolver)
{
    private const string BackendTypeDpapi = "WindowsDpapi";

    private sealed class CredentialRow
    {
        public int CredentialKey { get; init; }
        public required string CredentialName { get; init; }
        public required string BackendType { get; init; }
        public string? Username { get; init; }
        public string? ProtectedPassword { get; init; }
        public string? ScopePreference { get; init; }
        public string? CurrentScope { get; init; }
        public string? VaultSafe { get; init; }
        public string? VaultFolder { get; init; }
        public string? VaultObject { get; init; }
        public DateTime? ModifiedDate { get; init; }
    }

    private const string SelectSql = """
        SELECT CredentialKey, CredentialName, BackendType, Username, ProtectedPassword,
               ScopePreference, CurrentScope, VaultSafe, VaultFolder, VaultObject, ModifiedDate
        FROM web.credential
        """;

    /// <summary>Redacted -- for the admin API/UI. Never returns a usable password or full vault reference.</summary>
    public async Task<IReadOnlyList<Credential>> GetAllAsync()
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<CredentialRow>($"{SelectSql} ORDER BY CredentialName");
        return rows.Select(Redact).ToList();
    }

    public async Task<int> CreateAsync(SaveCredentialRequest request, int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            INSERT INTO web.credential
                (CredentialName, BackendType, Username, ProtectedPassword, ScopePreference, CurrentScope,
                 VaultSafe, VaultFolder, VaultObject, ModifiedBy, ModifiedDate)
            OUTPUT inserted.CredentialKey
            VALUES
                (@CredentialName, @BackendType, @Username, @ProtectedPassword, @ScopePreference, @CurrentScope,
                 @VaultSafe, @VaultFolder, @VaultObject, @ModifiedBy, SYSUTCDATETIME())
            """;
        return await connection.QuerySingleAsync<int>(sql, BuildSaveParameters(request, modifiedByUserKey));
    }

    public async Task UpdateAsync(int credentialKey, SaveCredentialRequest request, int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();

        // A blank PlaintextPassword means "leave the stored password alone" --
        // same pattern as IdentityProviderRepository.UpdateAsync/D-115's own
        // notification_config.SaveConfigAsync.
        var setPassword = request.BackendType == BackendTypeDpapi && !string.IsNullOrWhiteSpace(request.PlaintextPassword);
        var sql = setPassword
            ? """
              UPDATE web.credential
              SET CredentialName = @CredentialName, BackendType = @BackendType, Username = @Username,
                  ProtectedPassword = @ProtectedPassword, ScopePreference = @ScopePreference, CurrentScope = @CurrentScope,
                  VaultSafe = @VaultSafe, VaultFolder = @VaultFolder, VaultObject = @VaultObject,
                  ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
              WHERE CredentialKey = @CredentialKey
              """
            : """
              UPDATE web.credential
              SET CredentialName = @CredentialName, BackendType = @BackendType, Username = @Username,
                  ScopePreference = @ScopePreference,
                  VaultSafe = @VaultSafe, VaultFolder = @VaultFolder, VaultObject = @VaultObject,
                  ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
              WHERE CredentialKey = @CredentialKey
              """;

        var parameters = BuildSaveParameters(request, modifiedByUserKey);
        await connection.ExecuteAsync(sql, new
        {
            CredentialKey = credentialKey,
            parameters.CredentialName,
            parameters.BackendType,
            parameters.Username,
            parameters.ProtectedPassword,
            parameters.ScopePreference,
            parameters.CurrentScope,
            parameters.VaultSafe,
            parameters.VaultFolder,
            parameters.VaultObject,
            parameters.ModifiedBy
        });
    }

    public async Task DeleteAsync(int credentialKey)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("DELETE FROM web.credential WHERE CredentialKey = @CredentialKey", new { CredentialKey = credentialKey });
    }

    /// <summary>
    /// Unredacted -- only for internal use (SmtpNotificationSender,
    /// LdapGroupMemberResolver). Never expose this path's result to the
    /// admin API/UI.
    ///
    /// D-116's Machine-then-User scope upgrade: a DPAPI credential with
    /// ScopePreference = 'User' is always first saved with CurrentScope =
    /// 'Machine' (CreateAsync/UpdateAsync's own default, see
    /// BuildSaveParameters). Every request in this app runs under the same
    /// one app-pool identity (D-30) -- there's no separate "admin-interactive"
    /// identity a save-time encrypt could get wrong, so the very first
    /// successful decrypt here re-protects the plaintext under CurrentUser
    /// scope and persists the upgrade, converging automatically. Wrapped in
    /// try/catch so a transient failure (e.g. the app pool's Windows profile
    /// genuinely not loaded yet) just leaves it at Machine scope, retried on
    /// the next call, rather than breaking the actual credential retrieval.
    /// </summary>
    public async Task<(string? Username, string Password)> ResolveForUseAsync(int credentialKey)
    {
        using var connection = connectionFactory.Create();
        var row = await connection.QuerySingleAsync<CredentialRow>($"{SelectSql} WHERE CredentialKey = @CredentialKey", new { CredentialKey = credentialKey });

        if (row.BackendType != BackendTypeDpapi)
        {
            var provider = vaultSecretProviderResolver.ResolveByBackendType(row.BackendType);
            var result = await provider.GetSecretAsync(new SecretQuery(row.VaultSafe ?? "", row.VaultFolder ?? "", row.VaultObject ?? ""));
            return (result.UserName ?? row.Username, new string(result.Content));
        }

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

    private static CredentialScope? ParseScope(string? scope) =>
        Enum.TryParse<CredentialScope>(scope, out var parsed) ? parsed : null;

    // A plain class, not a tuple -- Dapper can't use a ValueTuple as a
    // parameters object either (the same "ValueTuple should not be used for
    // parameters" failure as its own result-mapping limitation, hit live
    // while testing this against the real SMTP credential).
    private sealed class CredentialSaveParameters
    {
        public required string CredentialName { get; init; }
        public required string BackendType { get; init; }
        public string? Username { get; init; }
        public string? ProtectedPassword { get; init; }
        public string? ScopePreference { get; init; }
        public string? CurrentScope { get; init; }
        public string? VaultSafe { get; init; }
        public string? VaultFolder { get; init; }
        public string? VaultObject { get; init; }
        public int? ModifiedBy { get; init; }
    }

    private CredentialSaveParameters BuildSaveParameters(SaveCredentialRequest request, int? modifiedByUserKey)
    {
        if (request.BackendType != BackendTypeDpapi)
        {
            return new CredentialSaveParameters
            {
                CredentialName = request.CredentialName,
                BackendType = request.BackendType,
                VaultSafe = request.VaultSafe,
                VaultFolder = request.VaultFolder,
                VaultObject = request.VaultObject,
                ModifiedBy = modifiedByUserKey
            };
        }

        var setPassword = !string.IsNullOrWhiteSpace(request.PlaintextPassword);
        return new CredentialSaveParameters
        {
            CredentialName = request.CredentialName,
            BackendType = request.BackendType,
            Username = request.Username,
            ProtectedPassword = setPassword ? localSecretProtector.Protect(request.PlaintextPassword!, CredentialScope.Machine) : null,
            ScopePreference = request.ScopePreference,
            CurrentScope = setPassword ? nameof(CredentialScope.Machine) : null,
            ModifiedBy = modifiedByUserKey
        };
    }

    private static Credential Redact(CredentialRow row) => new()
    {
        CredentialKey = row.CredentialKey,
        CredentialName = row.CredentialName,
        BackendType = row.BackendType,
        Username = row.Username,
        HasPassword = !string.IsNullOrEmpty(row.ProtectedPassword),
        ScopePreference = row.ScopePreference,
        CurrentScope = row.CurrentScope,
        VaultSafe = row.VaultSafe,
        VaultFolder = row.VaultFolder,
        VaultObject = row.VaultObject,
        ModifiedDate = row.ModifiedDate
    };
}
