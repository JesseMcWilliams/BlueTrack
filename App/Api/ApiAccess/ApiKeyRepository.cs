using BlueTrack.Api.Data;
using Dapper;

namespace BlueTrack.Api.ApiAccess;

public sealed record ApiKeySummary(
    int ApiKeyKey, string DisplayName, string KeyPrefix, int AppRoleKey, string RoleName,
    DateTime ExpiresAt, bool IsEnabled, DateTime CreatedAt, string? CreatedByName, DateTime? LastUsedAt, string? LastUsedFrom,
    string? OwnerEmail, int? RateLimitPerMinute);

/// <summary>What a token request needs: the stored hash and whether the key may be used now.</summary>
public sealed record ApiKeyCredential(int ApiKeyKey, string DisplayName, byte[] KeyHash, DateTime ExpiresAt, bool IsEnabled);

public sealed record ApiKeyState(bool IsEnabled, DateTime ExpiresAt, string DisplayName, int? RateLimitPerMinute);

public sealed record ApiKeyRights(string RoleName, IReadOnlyList<string> PermissionNames);

/// <summary>D-243: web.api_key. The key itself is never stored, only its hash.</summary>
public sealed class ApiKeyRepository(IDbConnectionFactory connectionFactory)
{
    private const string SummarySelect = """
        SELECT k.ApiKeyKey, k.DisplayName, k.KeyPrefix, k.AppRoleKey, r.RoleName, k.ExpiresAt, k.IsEnabled, k.CreatedAt,
               u.DisplayName AS CreatedByName, k.LastUsedAt, k.LastUsedFrom, k.OwnerEmail, k.RateLimitPerMinute
        FROM web.api_key k
        JOIN web.app_role r ON r.AppRoleKey = k.AppRoleKey
        LEFT JOIN web.app_user u ON u.UserKey = k.CreatedBy
        """;

    public async Task<IReadOnlyList<ApiKeySummary>> ListAsync()
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<ApiKeySummary>(SummarySelect + " ORDER BY k.DisplayName");
        return rows.AsList();
    }

    /// <summary>Enabled keys that expire after <paramref name="nowUtc"/> and within <paramref name="withinDays"/> days of it (for the expiry emails).</summary>
    public async Task<IReadOnlyList<ApiKeySummary>> ListExpiringAsync(DateTime nowUtc, int withinDays)
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<ApiKeySummary>(
            SummarySelect + " WHERE k.IsEnabled = 1 AND k.ExpiresAt > @nowUtc AND k.ExpiresAt <= DATEADD(DAY, @withinDays, @nowUtc) ORDER BY k.ExpiresAt",
            new { nowUtc, withinDays });
        return rows.AsList();
    }

    /// <summary>Enabled keys that expired within the last <paramref name="sinceDays"/> days before <paramref name="nowUtc"/> (for the expiry emails).</summary>
    public async Task<IReadOnlyList<ApiKeySummary>> ListRecentlyExpiredAsync(DateTime nowUtc, int sinceDays)
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<ApiKeySummary>(
            SummarySelect + " WHERE k.IsEnabled = 1 AND k.ExpiresAt <= @nowUtc AND k.ExpiresAt > DATEADD(DAY, -@sinceDays, @nowUtc) ORDER BY k.ExpiresAt",
            new { nowUtc, sinceDays });
        return rows.AsList();
    }

    public async Task<int> GetReminderLeadDaysAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>("SELECT ApiKeyReminderLeadDays FROM web.app_config");
    }

    public async Task<ApiKeySummary?> GetAsync(int apiKeyKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<ApiKeySummary>(SummarySelect + " WHERE k.ApiKeyKey = @apiKeyKey", new { apiKeyKey });
    }

    public async Task<ApiKeyCredential?> FindByPrefixAsync(string keyPrefix)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<ApiKeyCredential>(
            "SELECT ApiKeyKey, DisplayName, KeyHash, ExpiresAt, IsEnabled FROM web.api_key WHERE KeyPrefix = @keyPrefix", new { keyPrefix });
    }

    public async Task<ApiKeyState?> GetStateAsync(int apiKeyKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<ApiKeyState>(
            "SELECT IsEnabled, ExpiresAt, DisplayName, RateLimitPerMinute FROM web.api_key WHERE ApiKeyKey = @apiKeyKey", new { apiKeyKey });
    }

    /// <summary>The new key's number. A duplicate name is a unique-constraint error (the caller checks first).</summary>
    public async Task<int> CreateAsync(string displayName, string keyPrefix, byte[] keyHash, int appRoleKey, DateTime expiresAt, int createdBy,
        string? ownerEmail = null, int? rateLimitPerMinute = null)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>("""
            INSERT INTO web.api_key (DisplayName, KeyPrefix, KeyHash, AppRoleKey, ExpiresAt, CreatedBy, OwnerEmail, RateLimitPerMinute)
            OUTPUT inserted.ApiKeyKey
            VALUES (@displayName, @keyPrefix, @keyHash, @appRoleKey, @expiresAt, @createdBy, @ownerEmail, @rateLimitPerMinute)
            """, new { displayName, keyPrefix, keyHash, appRoleKey, expiresAt, createdBy, ownerEmail, rateLimitPerMinute });
    }

    /// <summary>Changes a key's name, role, owner email and rate limit, and its expiry when <paramref name="expiresAt"/> is given.</summary>
    public async Task UpdateAsync(int apiKeyKey, string displayName, int appRoleKey, DateTime? expiresAt, string? ownerEmail, int? rateLimitPerMinute)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("""
            UPDATE web.api_key
            SET DisplayName = @displayName, AppRoleKey = @appRoleKey, ExpiresAt = COALESCE(@expiresAt, ExpiresAt),
                OwnerEmail = @ownerEmail, RateLimitPerMinute = @rateLimitPerMinute
            WHERE ApiKeyKey = @apiKeyKey
            """, new { apiKeyKey, displayName, appRoleKey, expiresAt, ownerEmail, rateLimitPerMinute });
    }

    public async Task<bool> NameExistsAsync(string displayName)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<bool>(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM web.api_key WHERE DisplayName = @displayName) THEN 1 ELSE 0 END", new { displayName });
    }

    public async Task<bool> SetEnabledAsync(int apiKeyKey, bool isEnabled)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync("UPDATE web.api_key SET IsEnabled = @isEnabled WHERE ApiKeyKey = @apiKeyKey", new { apiKeyKey, isEnabled }) > 0;
    }

    public async Task<bool> DeleteAsync(int apiKeyKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync("DELETE FROM web.api_key WHERE ApiKeyKey = @apiKeyKey", new { apiKeyKey }) > 0;
    }

    public async Task TouchLastUsedAsync(int apiKeyKey, string? from)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("UPDATE web.api_key SET LastUsedAt = SYSUTCDATETIME(), LastUsedFrom = @from WHERE ApiKeyKey = @apiKeyKey",
            new { apiKeyKey, from = from is { Length: > 64 } ? from[..64] : from });
    }

    public async Task<int> GetDefaultLifetimeDaysAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>("SELECT ApiKeyDefaultLifetimeDays FROM web.app_config");
    }

    public async Task<string?> GetRoleNameAsync(int appRoleKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<string>("SELECT RoleName FROM web.app_role WHERE AppRoleKey = @appRoleKey", new { appRoleKey });
    }

    /// <summary>The key's role and that role's permissions minus <see cref="ApiAccessRules.NeverGranted"/>; null if the key is gone.</summary>
    public async Task<ApiKeyRights?> GetRightsAsync(int apiKeyKey)
    {
        using var connection = connectionFactory.Create();
        var roleName = await connection.QuerySingleOrDefaultAsync<string>("""
            SELECT r.RoleName FROM web.api_key k JOIN web.app_role r ON r.AppRoleKey = k.AppRoleKey WHERE k.ApiKeyKey = @apiKeyKey
            """, new { apiKeyKey });
        if (roleName is null) return null;

        var permissions = await connection.QueryAsync<string>("""
            SELECT DISTINCT p.PermissionName
            FROM web.api_key k
            JOIN web.role_permission rp ON rp.RoleKey = k.AppRoleKey
            JOIN web.app_permission p ON p.PermissionKey = rp.PermissionKey
            WHERE k.ApiKeyKey = @apiKeyKey
            """, new { apiKeyKey });
        return new ApiKeyRights(roleName, permissions.Where(p => !ApiAccessRules.NeverGranted.Contains(p)).ToList());
    }

    /// <summary>The 'BlueTrack API access (system)' user that a refused token request for an unknown key is attributed to.</summary>
    public async Task<int?> GetSystemUserKeyAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<int?>("""
            SELECT u.UserKey
            FROM web.app_user u
            JOIN web.identity_provider_config p ON p.ProviderKey = u.ProviderKey AND p.ProviderType = 'ApiAccess'
            WHERE u.ExternalIdentifier = 'apikey-system'
            """);
    }
}
