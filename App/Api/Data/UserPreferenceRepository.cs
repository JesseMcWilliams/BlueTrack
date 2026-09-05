using Dapper;

namespace BlueTrack.Api.Data;

/// <summary>
/// web.user_preference (Design_Accessibility_And_Theming.md, D-93): a
/// generalized per-user key/value preference store -- Theme is the first
/// consumer, but the shape isn't Theme-specific.
/// </summary>
public sealed class UserPreferenceRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyDictionary<string, string>> GetAllForUserAsync(int userKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = "SELECT PreferenceKey, PreferenceValue FROM web.user_preference WHERE UserKey = @UserKey";
        var rows = await connection.QueryAsync<(string PreferenceKey, string PreferenceValue)>(sql, new { UserKey = userKey });
        return rows.ToDictionary(r => r.PreferenceKey, r => r.PreferenceValue);
    }

    public async Task SetAsync(int userKey, string preferenceKey, string preferenceValue)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            MERGE web.user_preference AS target
            USING (SELECT @UserKey AS UserKey, @PreferenceKey AS PreferenceKey) AS source
                ON target.UserKey = source.UserKey AND target.PreferenceKey = source.PreferenceKey
            WHEN MATCHED THEN
                UPDATE SET PreferenceValue = @PreferenceValue, ModifiedDate = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (UserKey, PreferenceKey, PreferenceValue, ModifiedDate)
                VALUES (@UserKey, @PreferenceKey, @PreferenceValue, SYSUTCDATETIME());
            """;
        await connection.ExecuteAsync(sql, new { UserKey = userKey, PreferenceKey = preferenceKey, PreferenceValue = preferenceValue });
    }
}
