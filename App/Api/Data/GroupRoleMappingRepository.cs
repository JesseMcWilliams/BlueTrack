using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the Group → Role Mapping admin page. Only the WindowsIntegrated
/// provider is functional (AuthenticationExtensions.cs) -- create/resolve
/// hardcode it, matching UserRightsResolver/CurrentUserResolver's own
/// hardcoding, rather than exposing a provider picker for providers that
/// don't actually authenticate anyone yet.
/// </summary>
public sealed class GroupRoleMappingRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<GroupRoleMappingSummary>> GetAllAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT igrm.MappingKey, ipc.ProviderType, igrm.IdentityGroupName, r.RoleName
            FROM web.identity_group_role_map igrm
            JOIN web.identity_provider_config ipc ON ipc.ProviderKey = igrm.ProviderKey
            JOIN web.app_role r                    ON r.AppRoleKey = igrm.AppRoleKey
            ORDER BY ipc.ProviderType, r.RoleName
            """;
        var rows = await connection.QueryAsync<GroupRoleMappingSummary>(sql);
        return rows.AsList();
    }

    public async Task<int> CreateAsync(int providerKey, string sid, int roleKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            INSERT INTO web.identity_group_role_map (ProviderKey, IdentityGroupName, AppRoleKey)
            OUTPUT inserted.MappingKey
            VALUES (@ProviderKey, @Sid, @RoleKey)
            """;
        return await connection.QuerySingleAsync<int>(sql, new { ProviderKey = providerKey, Sid = sid, RoleKey = roleKey });
    }

    public async Task DeleteAsync(int mappingKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = "DELETE FROM web.identity_group_role_map WHERE MappingKey = @MappingKey";
        await connection.ExecuteAsync(sql, new { MappingKey = mappingKey });
    }

    public async Task<int?> GetRoleKeyByNameAsync(string roleName)
    {
        using var connection = connectionFactory.Create();
        const string sql = "SELECT AppRoleKey FROM web.app_role WHERE RoleName = @RoleName";
        return await connection.QuerySingleOrDefaultAsync<int?>(sql, new { RoleName = roleName });
    }
}
