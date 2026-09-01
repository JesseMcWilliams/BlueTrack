using Dapper;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the Claims Normalization Pipeline's steps 3-4
/// (Design_Authorization_Model.md): look up which roles a set of normalized
/// group identifiers map to for a given provider, then union those roles'
/// permissions (or role names, for display) together.
/// </summary>
public sealed class AuthorizationRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<string>> GetEffectivePermissionNamesAsync(
        int providerKey, IReadOnlyCollection<string> groupIdentifiers)
    {
        if (groupIdentifiers.Count == 0)
        {
            return [];
        }

        using var connection = connectionFactory.Create();

        const string sql = """
            SELECT DISTINCT p.PermissionName
            FROM web.identity_group_role_map igrm
            JOIN web.role_permission rp ON rp.RoleKey = igrm.AppRoleKey
            JOIN web.app_permission p    ON p.PermissionKey = rp.PermissionKey
            WHERE igrm.ProviderKey = @ProviderKey
              AND igrm.IdentityGroupName IN @GroupIdentifiers
            """;

        var rows = await connection.QueryAsync<string>(
            sql, new { ProviderKey = providerKey, GroupIdentifiers = groupIdentifiers });
        return rows.AsList();
    }

    public async Task<IReadOnlyList<string>> GetMatchedRoleNamesAsync(
        int providerKey, IReadOnlyCollection<string> groupIdentifiers)
    {
        if (groupIdentifiers.Count == 0)
        {
            return [];
        }

        using var connection = connectionFactory.Create();

        const string sql = """
            SELECT DISTINCT r.RoleName
            FROM web.identity_group_role_map igrm
            JOIN web.app_role r ON r.AppRoleKey = igrm.AppRoleKey
            WHERE igrm.ProviderKey = @ProviderKey
              AND igrm.IdentityGroupName IN @GroupIdentifiers
            """;

        var rows = await connection.QueryAsync<string>(
            sql, new { ProviderKey = providerKey, GroupIdentifiers = groupIdentifiers });
        return rows.AsList();
    }
}
