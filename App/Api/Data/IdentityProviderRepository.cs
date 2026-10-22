using Dapper;
using BlueTrack.Api.Models;
using BlueTrack.Api.Secrets;

namespace BlueTrack.Api.Data;

public sealed class IdentityProviderRepository(IDbConnectionFactory connectionFactory, ILocalSecretProtector localSecretProtector)
{
    private const string RedactedPlaceholder = "***";
    public async Task<IdentityProviderConfig?> GetByTypeAsync(string providerType)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT TOP (1) ProviderKey, ProviderType, DisplayName, IsEnabled, DisplayOrder, ConfigurationValues
            FROM web.identity_provider_config
            WHERE ProviderType = @ProviderType
            ORDER BY IsEnabled DESC, DisplayOrder, ProviderKey
            """;
        // D-205: the model allows two providers of one type; the enabled one with the lower DisplayOrder wins.
        return await connection.QuerySingleOrDefaultAsync<IdentityProviderConfig>(sql, new { ProviderType = providerType });
    }

    public async Task<IdentityProviderConfig?> GetByKeyAsync(int providerKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT ProviderKey, ProviderType, DisplayName, IsEnabled, DisplayOrder, ConfigurationValues
            FROM web.identity_provider_config
            WHERE ProviderKey = @ProviderKey
            """;
        return await connection.QuerySingleOrDefaultAsync<IdentityProviderConfig>(sql, new { ProviderKey = providerKey });
    }

    /// <summary>Every provider, enabled or not, with its non-secret settings (no SecretReference).</summary>
    public async Task<IReadOnlyList<IdentityProviderConfig>> GetAllConfigsAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT ProviderKey, ProviderType, DisplayName, IsEnabled, DisplayOrder, ConfigurationValues
            FROM web.identity_provider_config
            WHERE ProviderType <> 'ApiAccess'   -- D-243: not a sign-in provider
            ORDER BY DisplayOrder, ProviderKey
            """;
        var rows = await connection.QueryAsync<IdentityProviderConfig>(sql);
        return rows.AsList();
    }

    public static readonly string[] SignInProviderTypes = ["WindowsIntegrated", "OIDC", "SAML"];

    /// <summary>
    /// D-205: the default sign-in method's provider type, or null for none (the
    /// sign-in page lists the methods). Also null when the chosen type has no
    /// enabled provider, so a disabled default never blocks sign-in.
    /// </summary>
    public async Task<string?> GetDefaultSignInProviderAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT TOP (1) ac.DefaultSignInProvider
            FROM web.app_config ac
            WHERE EXISTS (SELECT 1 FROM web.identity_provider_config ipc WHERE ipc.ProviderType = ac.DefaultSignInProvider AND ipc.IsEnabled = 1)
            """;
        return await connection.QuerySingleOrDefaultAsync<string?>(sql);
    }

    /// <summary>D-205: the stored setting as-is (the admin page shows it even when that provider is disabled).</summary>
    public async Task<string?> GetStoredDefaultSignInProviderAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<string?>("SELECT TOP (1) DefaultSignInProvider FROM web.app_config");
    }

    public async Task SetDefaultSignInProviderAsync(string? providerType)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("UPDATE web.app_config SET DefaultSignInProvider = @ProviderType", new { ProviderType = providerType });
    }

    /// <summary>
    /// D-292: the method the sign-in page starts. With RememberLastSignInMethod on, the method this browser last
    /// signed in with (<paramref name="lastUsedProviderType"/>, from its cookie) while it's still an enabled sign-in
    /// method; otherwise the default (D-205), as <see cref="GetDefaultSignInProviderAsync"/>.
    /// </summary>
    public async Task<string?> GetEffectiveSignInProviderAsync(string? lastUsedProviderType)
    {
        if (lastUsedProviderType is null || !SignInProviderTypes.Contains(lastUsedProviderType))
        {
            return await GetDefaultSignInProviderAsync();
        }

        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT TOP (1)
                CASE WHEN ac.RememberLastSignInMethod = 1
                      AND EXISTS (SELECT 1 FROM web.identity_provider_config ipc WHERE ipc.ProviderType = @LastUsed AND ipc.IsEnabled = 1)
                     THEN @LastUsed
                     WHEN EXISTS (SELECT 1 FROM web.identity_provider_config ipc WHERE ipc.ProviderType = ac.DefaultSignInProvider AND ipc.IsEnabled = 1)
                     THEN ac.DefaultSignInProvider
                END
            FROM web.app_config ac
            """;
        return await connection.QuerySingleOrDefaultAsync<string?>(sql, new { LastUsed = lastUsedProviderType });
    }

    public async Task<bool> GetRememberLastSignInMethodAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<bool>("SELECT TOP (1) RememberLastSignInMethod FROM web.app_config");
    }

    public async Task SetRememberLastSignInMethodAsync(bool remember)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("UPDATE web.app_config SET RememberLastSignInMethod = @Remember", new { Remember = remember });
    }

    public async Task<IReadOnlyList<IdentityProviderConfig>> GetEnabledAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT ProviderKey, ProviderType, DisplayName, IsEnabled, DisplayOrder
            FROM web.identity_provider_config
            WHERE IsEnabled = 1 AND ProviderType <> 'ApiAccess'   -- D-243: not a sign-in provider
            ORDER BY DisplayOrder
            """;
        var rows = await connection.QueryAsync<IdentityProviderConfig>(sql);
        return rows.AsList();
    }

    /// <summary>Redacted (D-84) -- SecretReference is never returned to the admin UI once a value is set.</summary>
    public async Task<IReadOnlyList<IdentityProviderDetail>> GetAllAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT ProviderKey, ProviderType, DisplayName, IsEnabled, DisplayOrder, ConfigurationValues, SecretReference, ModifiedDate
            FROM web.identity_provider_config
            WHERE ProviderType <> 'ApiAccess'   -- D-243: not a sign-in provider
            ORDER BY DisplayOrder
            """;
        var rows = await connection.QueryAsync<IdentityProviderDetail>(sql);
        return rows.Select(Redact).ToList();
    }

    public async Task<int> CreateAsync(SaveIdentityProviderRequest request)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            INSERT INTO web.identity_provider_config
                (ProviderType, DisplayName, IsEnabled, DisplayOrder, ConfigurationValues, SecretReference)
            OUTPUT inserted.ProviderKey
            VALUES (@ProviderType, @DisplayName, @IsEnabled, @DisplayOrder, @ConfigurationValues, @SecretReference)
            """;
        return await connection.QuerySingleAsync<int>(sql, new
        {
            request.ProviderType,
            request.DisplayName,
            request.IsEnabled,
            request.DisplayOrder,
            request.ConfigurationValues,
            SecretReference = ResolveSecretReference(request)
        });
    }

    public async Task UpdateAsync(int providerKey, SaveIdentityProviderRequest request)
    {
        using var connection = connectionFactory.Create();

        // A plain SecretReference/PlaintextSecret omission on update means
        // "leave the stored credential alone" -- only overwrite it when the
        // admin actually supplied a new PlaintextSecret this time.
        var setSecretReference = !string.IsNullOrWhiteSpace(request.PlaintextSecret);
        var sql = setSecretReference
            ? """
              UPDATE web.identity_provider_config
              SET ProviderType = @ProviderType, DisplayName = @DisplayName, IsEnabled = @IsEnabled,
                  DisplayOrder = @DisplayOrder, ConfigurationValues = @ConfigurationValues,
                  SecretReference = @SecretReference, ModifiedDate = SYSUTCDATETIME()
              WHERE ProviderKey = @ProviderKey
              """
            : """
              UPDATE web.identity_provider_config
              SET ProviderType = @ProviderType, DisplayName = @DisplayName, IsEnabled = @IsEnabled,
                  DisplayOrder = @DisplayOrder, ConfigurationValues = @ConfigurationValues,
                  ModifiedDate = SYSUTCDATETIME()
              WHERE ProviderKey = @ProviderKey
              """;

        await connection.ExecuteAsync(sql, new
        {
            ProviderKey = providerKey,
            request.ProviderType,
            request.DisplayName,
            request.IsEnabled,
            request.DisplayOrder,
            request.ConfigurationValues,
            SecretReference = ResolveSecretReference(request)
        });
    }

    private string? ResolveSecretReference(SaveIdentityProviderRequest request) =>
        string.IsNullOrWhiteSpace(request.PlaintextSecret)
            ? request.SecretReference
            : localSecretProtector.Protect(request.PlaintextSecret);

    private static IdentityProviderDetail Redact(IdentityProviderDetail detail) =>
        string.IsNullOrEmpty(detail.SecretReference)
            ? detail
            : new IdentityProviderDetail
            {
                ProviderKey = detail.ProviderKey,
                ProviderType = detail.ProviderType,
                DisplayName = detail.DisplayName,
                IsEnabled = detail.IsEnabled,
                DisplayOrder = detail.DisplayOrder,
                ConfigurationValues = detail.ConfigurationValues,
                SecretReference = RedactedPlaceholder,
                ModifiedDate = detail.ModifiedDate
            };

    public async Task DeleteAsync(int providerKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = "DELETE FROM web.identity_provider_config WHERE ProviderKey = @ProviderKey";
        await connection.ExecuteAsync(sql, new { ProviderKey = providerKey });
    }
}
