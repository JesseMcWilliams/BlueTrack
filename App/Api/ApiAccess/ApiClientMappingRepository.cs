using BlueTrack.Api.Data;
using Dapper;

namespace BlueTrack.Api.ApiAccess;

public sealed record ApiClientMappingSummary(
    int ApiClientMappingKey, int ProviderKey, string ProviderName, string MatchType, string MatchValue, int AppRoleKey, string RoleName,
    string? DisplayName, int DisplayOrder, bool IsEnabled, DateTime CreatedAt, string? CreatedByName, DateTime? LastUsedAt);

/// <summary>What a token exchange needs from a matching mapping.</summary>
public sealed record ApiClientMappingMatch(int ApiClientMappingKey, string MatchType, string MatchValue, int AppRoleKey, string RoleName, string? DisplayName);

public sealed record ApiClientMappingState(bool IsEnabled, int AppRoleKey);

/// <summary>D-243 (phase 2): web.api_client_mapping, the role a caller with an identity provider's token gets.</summary>
public sealed class ApiClientMappingRepository(IDbConnectionFactory connectionFactory)
{
    private const string SummarySelect = """
        SELECT m.ApiClientMappingKey, m.ProviderKey, p.DisplayName AS ProviderName, m.MatchType, m.MatchValue, m.AppRoleKey, r.RoleName,
               m.DisplayName, m.DisplayOrder, m.IsEnabled, m.CreatedAt, u.DisplayName AS CreatedByName, m.LastUsedAt
        FROM web.api_client_mapping m
        JOIN web.identity_provider_config p ON p.ProviderKey = m.ProviderKey
        JOIN web.app_role r ON r.AppRoleKey = m.AppRoleKey
        LEFT JOIN web.app_user u ON u.UserKey = m.CreatedBy
        """;

    public async Task<IReadOnlyList<ApiClientMappingSummary>> ListAsync()
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<ApiClientMappingSummary>(SummarySelect + " ORDER BY p.DisplayName, m.DisplayOrder, m.MatchType, m.MatchValue");
        return rows.AsList();
    }

    public async Task<ApiClientMappingSummary?> GetAsync(int apiClientMappingKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<ApiClientMappingSummary>(SummarySelect + " WHERE m.ApiClientMappingKey = @apiClientMappingKey", new { apiClientMappingKey });
    }

    /// <summary>
    /// The enabled mapping a token gets: the lowest DisplayOrder among those matching the client id
    /// or any of the groups, a ClientId before a Group when the order is equal. Null when none match.
    /// </summary>
    public async Task<ApiClientMappingMatch?> FindMatchAsync(int providerKey, string? clientId, IReadOnlyCollection<string> groups)
    {
        using var connection = connectionFactory.Create();
        return await connection.QueryFirstOrDefaultAsync<ApiClientMappingMatch>("""
            SELECT TOP (1) m.ApiClientMappingKey, m.MatchType, m.MatchValue, m.AppRoleKey, r.RoleName, m.DisplayName
            FROM web.api_client_mapping m
            JOIN web.app_role r ON r.AppRoleKey = m.AppRoleKey
            WHERE m.ProviderKey = @providerKey AND m.IsEnabled = 1
              AND ((m.MatchType = 'ClientId' AND @clientId IS NOT NULL AND m.MatchValue = @clientId)
                   OR (m.MatchType = 'Group' AND m.MatchValue IN @groups))
            ORDER BY m.DisplayOrder, CASE m.MatchType WHEN 'ClientId' THEN 0 ELSE 1 END, m.ApiClientMappingKey
            """, new { providerKey, clientId, groups = groups.Count == 0 ? new[] { "" } : groups.ToArray() });
    }

    public async Task<ApiClientMappingState?> GetStateAsync(int apiClientMappingKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<ApiClientMappingState>(
            "SELECT IsEnabled, AppRoleKey FROM web.api_client_mapping WHERE ApiClientMappingKey = @apiClientMappingKey", new { apiClientMappingKey });
    }

    public async Task<bool> ExistsAsync(int providerKey, string matchType, string matchValue)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<bool>("""
            SELECT CASE WHEN EXISTS (SELECT 1 FROM web.api_client_mapping WHERE ProviderKey = @providerKey AND MatchType = @matchType AND MatchValue = @matchValue) THEN 1 ELSE 0 END
            """, new { providerKey, matchType, matchValue });
    }

    public async Task<int> CreateAsync(int providerKey, string matchType, string matchValue, int appRoleKey, string? displayName, int displayOrder, int createdBy)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>("""
            INSERT INTO web.api_client_mapping (ProviderKey, MatchType, MatchValue, AppRoleKey, DisplayName, DisplayOrder, CreatedBy)
            OUTPUT inserted.ApiClientMappingKey
            VALUES (@providerKey, @matchType, @matchValue, @appRoleKey, @displayName, @displayOrder, @createdBy)
            """, new { providerKey, matchType, matchValue, appRoleKey, displayName, displayOrder, createdBy });
    }

    public async Task<bool> SetEnabledAsync(int apiClientMappingKey, bool isEnabled)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync("UPDATE web.api_client_mapping SET IsEnabled = @isEnabled WHERE ApiClientMappingKey = @apiClientMappingKey", new { apiClientMappingKey, isEnabled }) > 0;
    }

    public async Task<bool> DeleteAsync(int apiClientMappingKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync("DELETE FROM web.api_client_mapping WHERE ApiClientMappingKey = @apiClientMappingKey", new { apiClientMappingKey }) > 0;
    }

    public async Task TouchLastUsedAsync(int apiClientMappingKey)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("UPDATE web.api_client_mapping SET LastUsedAt = SYSUTCDATETIME() WHERE ApiClientMappingKey = @apiClientMappingKey", new { apiClientMappingKey });
    }

    /// <summary>A role's permissions minus what an API caller can never have; null when the role is gone or is Admin.</summary>
    public async Task<ApiKeyRights?> GetRoleRightsAsync(int appRoleKey)
    {
        using var connection = connectionFactory.Create();
        var roleName = await connection.QuerySingleOrDefaultAsync<string>("SELECT RoleName FROM web.app_role WHERE AppRoleKey = @appRoleKey", new { appRoleKey });
        if (roleName is null || roleName == ApiAccessRules.AdminRoleName) return null;

        var permissions = await connection.QueryAsync<string>("""
            SELECT DISTINCT p.PermissionName
            FROM web.role_permission rp
            JOIN web.app_permission p ON p.PermissionKey = rp.PermissionKey
            WHERE rp.RoleKey = @appRoleKey
            """, new { appRoleKey });
        return new ApiKeyRights(roleName, permissions.Where(p => !ApiAccessRules.NeverGranted.Contains(p)).ToList());
    }
}
