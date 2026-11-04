using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// D-335: Reports > Safe Administrators, the least-privilege view: the people who hold Manage Safe or Manage Safe
/// Members on a Safe, directly or through a Vault or AD group (dbo.vw_safe_access_people), with whether they hold
/// every Safe permission (<see cref="FullRightsPermissions"/>). One row per Safe and person; the ways they reach it
/// are joined. A row is built-in when the person is one of CyberArk's own users (the user's choice, 2026-11-04: built-in
/// groups, such as Vault Admins, and the people in them are shown; built-in users are hidden unless asked for).
/// </summary>
public sealed class SafeAdministratorsRepository(IDbConnectionFactory connectionFactory)
{
    /// <summary>
    /// "Every permission": the Safe permissions both CyberArk sources grant. Left out: the request-authorization levels
    /// (an approver's role, not a right on the Safe's accounts) and the move permissions, which the sources split
    /// differently (MoveAccountsAndFolders, or MoveAccountsFrom and MoveAccountsInto).
    /// </summary>
    public static readonly string[] FullRightsPermissions =
    [
        "UseAccounts", "RetrieveAccounts", "ListAccounts", "AddAccounts", "UpdateAccountContent", "UpdateAccountProperties",
        "InitiateCPMAccountManagementOperations", "SpecifyNextAccountContent", "RenameAccounts", "DeleteAccounts", "UnlockAccounts",
        "ManageSafe", "ManageSafeMembers", "BackupSafe", "ViewAuditLog", "ViewSafeMembers", "AccessWithoutConfirmation",
        "CreateFolders", "DeleteFolders"
    ];

    /// <summary>
    /// CyberArk's own user types (dim_user.UserType): Self-Hosted's numbers as SelfHostedStagingMapper maps them and
    /// Privilege Cloud's names. Applications (35, AIMAccount) are the customer's own and stay listed.
    /// </summary>
    public static readonly string[] BuiltInUserTypes =
    [
        "10", "11", "31", "32", "33", "36", "504",
        "Built-InAdmins", "ENE", "CPM", "PVWA", "AppProvider", "PSM", "DRUser", "CyberArkServiceUser", "Install", "Telemetry", "SaaSSRV"
    ];

    public async Task<IReadOnlyList<SafeAdministratorRow>> GetAsync()
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<SafeAdministratorRow>("""
            WITH perms AS (
                SELECT b.EntitlementKey,
                       MAX(CASE WHEN p.PermissionName = 'ManageSafe' AND b.IsGranted = 1 THEN 1 ELSE 0 END) AS ManageSafe,
                       MAX(CASE WHEN p.PermissionName = 'ManageSafeMembers' AND b.IsGranted = 1 THEN 1 ELSE 0 END) AS ManageSafeMembers,
                       SUM(CASE WHEN p.PermissionName IN @FullRights AND b.IsGranted = 1 THEN 1 ELSE 0 END) AS FullCount
                FROM dbo.bridge_entitlement_permission b
                JOIN dbo.dim_permission p ON p.PermissionKey = b.PermissionKey
                GROUP BY b.EntitlementKey
            ),
            ways AS (
                SELECT ap.SafeKey, ap.PersonName, ap.DisplayName, ap.IsEnabled, ap.AccessPath,
                       pm.ManageSafe, pm.ManageSafeMembers, CASE WHEN pm.FullCount = @FullRightsCount THEN 1 ELSE 0 END AS FullRights,
                       CASE WHEN (fse.MemberType = 'User' AND fse.IsPredefinedUser = 1) OR pu.UserType IN @BuiltInTypes
                            THEN 1 ELSE 0 END AS IsBuiltIn
                FROM dbo.vw_safe_access_people ap
                JOIN perms pm ON pm.EntitlementKey = ap.EntitlementKey
                JOIN dbo.fact_safe_entitlement fse ON fse.EntitlementKey = ap.EntitlementKey
                LEFT JOIN dbo.dim_user pu ON pu.UserKey = ap.VaultUserKey    -- the person, when a Vault user
                WHERE pm.ManageSafe = 1 OR pm.ManageSafeMembers = 1
            )
            SELECT w.SafeKey, ds.SafeName, src.SourceSystemName, w.PersonName, MAX(w.DisplayName) AS DisplayName,
                   CAST(MAX(CAST(w.IsEnabled AS INT)) AS BIT) AS IsEnabled,
                   STRING_AGG(CAST(w.AccessPath AS NVARCHAR(MAX)), N'; ') WITHIN GROUP (ORDER BY w.AccessPath) AS AccessPaths,
                   CAST(MAX(w.ManageSafe) AS BIT) AS ManageSafe, CAST(MAX(w.ManageSafeMembers) AS BIT) AS ManageSafeMembers,
                   CAST(MAX(w.FullRights) AS BIT) AS FullRights, CAST(MIN(w.IsBuiltIn) AS BIT) AS IsBuiltIn
            FROM ways w
            JOIN dbo.dim_safe ds ON ds.SafeKey = w.SafeKey
            JOIN dbo.dim_source_system src ON src.SourceSystemKey = ds.SourceSystemKey
            GROUP BY w.SafeKey, ds.SafeName, src.SourceSystemName, w.PersonName
            ORDER BY ds.SafeName, w.SafeKey, w.PersonName;
            """, new { FullRights = FullRightsPermissions, FullRightsCount = FullRightsPermissions.Length, BuiltInTypes = BuiltInUserTypes },
            commandTimeout: 120);
        return rows.AsList();
    }

    /// <summary>D-335: one row per person, over the rows given (already filtered), most Safes with full rights first.</summary>
    public static IReadOnlyList<SafeAdministratorPerson> ByPerson(IEnumerable<SafeAdministratorRow> rows) =>
        rows.GroupBy(r => r.PersonName, StringComparer.OrdinalIgnoreCase)
            .Select(g => new SafeAdministratorPerson
            {
                PersonName = g.First().PersonName,
                DisplayName = g.Select(r => r.DisplayName).FirstOrDefault(n => !string.IsNullOrEmpty(n)),
                IsEnabled = g.Any(r => r.IsEnabled),
                IsBuiltIn = g.All(r => r.IsBuiltIn),
                Safes = g.Count(),
                ManageSafeSafes = g.Count(r => r.ManageSafe),
                ManageSafeMembersSafes = g.Count(r => r.ManageSafeMembers),
                FullRightsSafes = g.Count(r => r.FullRights)
            })
            .OrderByDescending(p => p.FullRightsSafes).ThenByDescending(p => p.Safes).ThenBy(p => p.PersonName, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
