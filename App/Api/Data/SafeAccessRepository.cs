using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// D-281/D-282 (48_BlueTrack_SelfHostedDirectExtras.sql): who can reach an account's Safe, the CyberArk Applications
/// that can retrieve it, and the CyberArk Applications list.
/// </summary>
public sealed class SafeAccessRepository(IDbConnectionFactory connectionFactory)
{
    /// <summary>Null when there's no such account. An application's own Vault user (type 35) is listed under Applications, not People.</summary>
    public async Task<AccountSafeAccess?> GetForAccountAsync(long accountKey)
    {
        using var connection = connectionFactory.Create();
        using var multi = await connection.QueryMultipleAsync("""
            SELECT COUNT(*) FROM dbo.fact_account WHERE AccountKey = @accountKey;

            SELECT p.PersonName, MAX(p.DisplayName) AS DisplayName, p.DomainName, p.SamAccountName, CAST(MAX(CAST(p.IsEnabled AS INT)) AS BIT) AS IsEnabled,
                   p.AccessPath, fse.GroupKey,   -- D-339: the group they come through, null for a direct member
                   src.UserSource,
                   CAST(MAX(CASE WHEN dp.PermissionName = 'UseAccounts' AND bep.IsGranted = 1 THEN 1 ELSE 0 END) AS BIT) AS CanUse,
                   CAST(MAX(CASE WHEN dp.PermissionName = 'RetrieveAccounts' AND bep.IsGranted = 1 THEN 1 ELSE 0 END) AS BIT) AS CanRetrieve,
                   CAST(MAX(CASE WHEN dp.PermissionName = 'ListAccounts' AND bep.IsGranted = 1 THEN 1 ELSE 0 END) AS BIT) AS CanList
            FROM dbo.fact_account fa
            JOIN dbo.vw_safe_access_people p ON p.SafeKey = fa.SafeKey
            JOIN dbo.fact_safe_entitlement fse ON fse.EntitlementKey = p.EntitlementKey
            LEFT JOIN dbo.bridge_entitlement_permission bep ON bep.EntitlementKey = p.EntitlementKey
            LEFT JOIN dbo.dim_permission dp ON dp.PermissionKey = bep.PermissionKey
            -- D-360: the user's source: the PVWA API says CyberArk or LDAP, the Self-Hosted export Internal or External.
            LEFT JOIN dbo.dim_user vu ON vu.UserKey = p.VaultUserKey
            CROSS APPLY (SELECT CASE
                WHEN p.VaultUserKey IS NULL AND p.SamAccountName IS NOT NULL THEN N'LDAP'
                WHEN vu.UserSource IN (N'CyberArk', N'Internal') THEN N'Vault'
                WHEN vu.UserSource IN (N'LDAP', N'External') THEN N'LDAP'
                ELSE NULLIF(LTRIM(RTRIM(vu.UserSource)), N'') END AS UserSource) src
            WHERE fa.AccountKey = @accountKey AND ISNULL(p.UserType, N'') <> N'35'
            GROUP BY p.PersonName, p.DomainName, p.SamAccountName, p.AccessPath, fse.GroupKey, src.UserSource
            ORDER BY CASE WHEN p.AccessPath = N'Direct' THEN 0 ELSE 1 END, p.AccessPath, p.PersonName;

            -- D-339: each group that is a member of the Safe, with its rights and how its AD members were last read, also
            -- when none were (a group not found in AD, or one whose read failed, has no member rows).
            SELECT g.GroupKey, g.GroupName,
                   CAST(MAX(CASE WHEN dp.PermissionName = 'UseAccounts' AND bep.IsGranted = 1 THEN 1 ELSE 0 END) AS BIT) AS CanUse,
                   CAST(MAX(CASE WHEN dp.PermissionName = 'RetrieveAccounts' AND bep.IsGranted = 1 THEN 1 ELSE 0 END) AS BIT) AS CanRetrieve,
                   CAST(MAX(CASE WHEN dp.PermissionName = 'ListAccounts' AND bep.IsGranted = 1 THEN 1 ELSE 0 END) AS BIT) AS CanList,
                   r.Status AS AdReadStatus, r.Message AS AdReadMessage, r.DomainName AS AdDomainName
            FROM dbo.fact_account fa
            JOIN dbo.fact_safe_entitlement fse ON fse.SafeKey = fa.SafeKey AND fse.MemberType = 'Group'
            JOIN dbo.dim_group g ON g.GroupKey = fse.GroupKey
            LEFT JOIN dbo.ad_group_read r ON r.GroupKey = g.GroupKey
            LEFT JOIN dbo.bridge_entitlement_permission bep ON bep.EntitlementKey = fse.EntitlementKey
            LEFT JOIN dbo.dim_permission dp ON dp.PermissionKey = bep.PermissionKey
            WHERE fa.AccountKey = @accountKey
            GROUP BY g.GroupKey, g.GroupName, r.Status, r.Message, r.DomainName
            ORDER BY g.GroupName;

            SELECT DISTINCT v.CyberArkApplicationKey, v.AppId, ca.Description, ca.Disabled, v.AccessPath
            FROM dbo.vw_cyberark_application_account_access v
            JOIN dbo.dim_cyberark_application ca ON ca.CyberArkApplicationKey = v.CyberArkApplicationKey
            WHERE v.AccountKey = @accountKey
            ORDER BY v.AppId, v.AccessPath;

            SELECT MAX(r.LastAttemptAt)
            FROM dbo.fact_account fa
            JOIN dbo.fact_safe_entitlement fse ON fse.SafeKey = fa.SafeKey AND fse.MemberType = 'Group'
            JOIN dbo.ad_group_read r ON r.GroupKey = fse.GroupKey
            WHERE fa.AccountKey = @accountKey;
            """, new { accountKey });
        if (await multi.ReadSingleAsync<int>() == 0) return null;
        return new AccountSafeAccess
        {
            People = (await multi.ReadAsync<SafeAccessPerson>()).ToList(),
            Groups = (await multi.ReadAsync<SafeAccessGroup>()).ToList(),
            Applications = (await multi.ReadAsync<AccountCyberArkApplication>()).ToList(),
            AdReadAt = await multi.ReadSingleOrDefaultAsync<DateTime?>()
        };
    }

    /// <summary>Every CyberArk Application, deleted ones last, with its methods and the accounts (not deleted) and Safes it can retrieve from.</summary>
    public async Task<IReadOnlyList<CyberArkApplicationSummary>> GetApplicationsAsync()
    {
        using var connection = connectionFactory.Create();
        using var multi = await connection.QueryMultipleAsync("""
            SELECT a.CyberArkApplicationKey, a.AppId, a.Description, a.Location, a.Disabled, a.ExpirationDate, a.AccessPermittedFrom, a.AccessPermittedTo,
                   a.AllowExtendedAuthenticationRestrictions,
                   NULLIF(LTRIM(CONCAT(a.BusinessOwnerFirstName, N' ', a.BusinessOwnerLastName)), N'') AS BusinessOwnerName,
                   a.BusinessOwnerEmail, a.BusinessOwnerPhone, a.IsDeletedInSource,
                   ISNULL(x.AccountCount, 0) AS AccountCount, ISNULL(x.SafeCount, 0) AS SafeCount
            FROM dbo.dim_cyberark_application a
            OUTER APPLY (SELECT COUNT(DISTINCT v.AccountKey) AS AccountCount, COUNT(DISTINCT fa.SafeKey) AS SafeCount
                         FROM dbo.vw_cyberark_application_account_access v
                         JOIN dbo.fact_account fa ON fa.AccountKey = v.AccountKey AND fa.IsDeleted = 0
                         WHERE v.CyberArkApplicationKey = a.CyberArkApplicationKey) x
            ORDER BY a.IsDeletedInSource, a.AppId;

            SELECT CyberArkApplicationKey, AuthType, AuthValue, Comment, IsFolder, AllowInternalScripts, Subject, Issuer, SubjectAlternativeName
            FROM dbo.dim_cyberark_application_authentication
            ORDER BY CyberArkApplicationKey, AuthType, AuthId;
            """);
        var apps = (await multi.ReadAsync<CyberArkApplicationSummary>()).ToList();
        var authentications = (await multi.ReadAsync<CyberArkApplicationAuthentication>()).ToLookup(a => a.CyberArkApplicationKey);
        foreach (var app in apps) app.Authentications = authentications[app.CyberArkApplicationKey].ToList();
        return apps;
    }
}
