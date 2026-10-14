using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>D-216: web.break_glass_account (16_BlueTrack_BreakGlassAccounts.sql) -- accounts whose sign-in raises an alert.</summary>
public sealed class BreakGlassAccountRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<BreakGlassAccount>> GetAllAsync()
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<BreakGlassAccount>("""
            SELECT b.BreakGlassAccountKey, b.ExternalIdentifier, b.DisplayName, b.Notes, b.CreatedDate, u.DisplayName AS CreatedByName
            FROM web.break_glass_account b
            LEFT JOIN web.app_user u ON u.UserKey = b.CreatedBy
            ORDER BY b.DisplayName
            """)).ToList();
    }

    public async Task<bool> IsBreakGlassAsync(string externalIdentifier)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteScalarAsync<bool>(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM web.break_glass_account WHERE ExternalIdentifier = @externalIdentifier) THEN 1 ELSE 0 END",
            new { externalIdentifier });
    }

    public async Task<int> CreateAsync(string externalIdentifier, string displayName, string? notes, int userKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>("""
            INSERT INTO web.break_glass_account (ExternalIdentifier, DisplayName, Notes, CreatedBy)
            OUTPUT inserted.BreakGlassAccountKey
            VALUES (@externalIdentifier, @displayName, @notes, @userKey)
            """, new { externalIdentifier, displayName, notes, userKey });
    }

    /// <summary>The removed row's display name, or null if there was none.</summary>
    public async Task<string?> DeleteAsync(int breakGlassAccountKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<string?>(
            "DELETE FROM web.break_glass_account OUTPUT deleted.DisplayName WHERE BreakGlassAccountKey = @breakGlassAccountKey",
            new { breakGlassAccountKey });
    }
}
