using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the Roles & Permissions admin page. web.app_permission's catalog
/// itself is confirmed/fixed (D-05, D-61) and not editable here -- only
/// app_role and which permissions each role bundles (role_permission).
/// </summary>
public sealed class RoleRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<PermissionCatalogItem>> GetPermissionCatalogAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = "SELECT PermissionKey, PermissionName, Description FROM web.app_permission ORDER BY PermissionName";
        var rows = await connection.QueryAsync<PermissionCatalogItem>(sql);
        return rows.AsList();
    }

    public async Task<IReadOnlyList<AppRoleSummary>> GetRolesAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT r.AppRoleKey, r.RoleName, r.Description, p.PermissionName
            FROM web.app_role r
            LEFT JOIN web.role_permission rp ON rp.RoleKey = r.AppRoleKey
            LEFT JOIN web.app_permission p    ON p.PermissionKey = rp.PermissionKey
            ORDER BY r.RoleName, p.PermissionName
            """;

        var rows = await connection.QueryAsync<(int AppRoleKey, string RoleName, string? Description, string? PermissionName)>(sql);

        return rows
            .GroupBy(r => (r.AppRoleKey, r.RoleName, r.Description))
            .Select(g => new AppRoleSummary
            {
                AppRoleKey = g.Key.AppRoleKey,
                RoleName = g.Key.RoleName,
                Description = g.Key.Description,
                PermissionNames = g.Where(r => r.PermissionName is not null).Select(r => r.PermissionName!).ToList()
            })
            .ToList();
    }

    public async Task<int> CreateRoleAsync(SaveRoleRequest request)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var roleKey = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.app_role (RoleName, Description) OUTPUT inserted.AppRoleKey VALUES (@RoleName, @Description)",
            new { request.RoleName, request.Description }, transaction);

        await InsertRolePermissionsAsync(connection, transaction, roleKey, request.PermissionNames);

        transaction.Commit();
        return roleKey;
    }

    public async Task UpdateRoleAsync(int roleKey, SaveRoleRequest request)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(
            "UPDATE web.app_role SET RoleName = @RoleName, Description = @Description WHERE AppRoleKey = @RoleKey",
            new { RoleKey = roleKey, request.RoleName, request.Description }, transaction);

        await connection.ExecuteAsync(
            "DELETE FROM web.role_permission WHERE RoleKey = @RoleKey", new { RoleKey = roleKey }, transaction);

        await InsertRolePermissionsAsync(connection, transaction, roleKey, request.PermissionNames);

        transaction.Commit();
    }

    public async Task DeleteRoleAsync(int roleKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(
            "DELETE FROM web.role_permission WHERE RoleKey = @RoleKey", new { RoleKey = roleKey }, transaction);
        // identity_group_role_map rows referencing this role (if any) block this delete via FK --
        // surfaces as a clean error rather than silently orphaning a group mapping.
        await connection.ExecuteAsync(
            "DELETE FROM web.app_role WHERE AppRoleKey = @RoleKey", new { RoleKey = roleKey }, transaction);

        transaction.Commit();
    }

    private static async Task InsertRolePermissionsAsync(
        System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, int roleKey, IReadOnlyList<string> permissionNames)
    {
        if (permissionNames.Count == 0)
        {
            return;
        }

        const string sql = """
            INSERT INTO web.role_permission (RoleKey, PermissionKey)
            SELECT @RoleKey, PermissionKey FROM web.app_permission WHERE PermissionName IN @PermissionNames
            """;
        await connection.ExecuteAsync(sql, new { RoleKey = roleKey, PermissionNames = permissionNames }, transaction);
    }
}
