using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

public sealed class IdentityProviderRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IdentityProviderConfig?> GetByTypeAsync(string providerType)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT ProviderKey, ProviderType, DisplayName, IsEnabled, DisplayOrder
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

    public async Task<IReadOnlyList<IdentityProviderDetail>> GetAllAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT ProviderKey, ProviderType, DisplayName, IsEnabled, DisplayOrder, ConfigurationValues, SecretReference
            FROM web.identity_provider_config
            ORDER BY DisplayOrder
            """;
        var rows = await connection.QueryAsync<IdentityProviderDetail>(sql);
        return rows.AsList();
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
        return await connection.QuerySingleAsync<int>(sql, request);
    }

    public async Task UpdateAsync(int providerKey, SaveIdentityProviderRequest request)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            UPDATE web.identity_provider_config
            SET ProviderType = @ProviderType, DisplayName = @DisplayName, IsEnabled = @IsEnabled,
                DisplayOrder = @DisplayOrder, ConfigurationValues = @ConfigurationValues,
                SecretReference = @SecretReference, ModifiedDate = SYSUTCDATETIME()
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
            request.SecretReference
        });
    }

    public async Task DeleteAsync(int providerKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = "DELETE FROM web.identity_provider_config WHERE ProviderKey = @ProviderKey";
        await connection.ExecuteAsync(sql, new { ProviderKey = providerKey });
    }
}
