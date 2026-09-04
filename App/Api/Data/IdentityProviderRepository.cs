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
            SELECT ProviderKey, ProviderType, DisplayName, IsEnabled, DisplayOrder, ConfigurationValues
            FROM web.identity_provider_config
            WHERE ProviderType = @ProviderType
            """;
        return await connection.QuerySingleOrDefaultAsync<IdentityProviderConfig>(sql, new { ProviderType = providerType });
    }

    public async Task<IReadOnlyList<IdentityProviderConfig>> GetEnabledAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT ProviderKey, ProviderType, DisplayName, IsEnabled, DisplayOrder
            FROM web.identity_provider_config
            WHERE IsEnabled = 1
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
            SELECT ProviderKey, ProviderType, DisplayName, IsEnabled, DisplayOrder, ConfigurationValues, SecretReference
            FROM web.identity_provider_config
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
                SecretReference = RedactedPlaceholder
            };

    public async Task DeleteAsync(int providerKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = "DELETE FROM web.identity_provider_config WHERE ProviderKey = @ProviderKey";
        await connection.ExecuteAsync(sql, new { ProviderKey = providerKey });
    }
}
