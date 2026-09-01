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
}
