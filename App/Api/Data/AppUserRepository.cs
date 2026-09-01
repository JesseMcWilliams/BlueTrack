using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Implements the app_user upsert from the Login Flow (Design_Authentication_Architecture.md,
/// step 6): insert on first login, refresh LastLogin on every one after that.
/// </summary>
public sealed class AppUserRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<AppUser> UpsertOnLoginAsync(int providerKey, string externalIdentifier, string? displayName, string? email)
    {
        using var connection = connectionFactory.Create();

        const string sql = """
            MERGE web.app_user AS target
            USING (SELECT @ProviderKey AS ProviderKey, @ExternalIdentifier AS ExternalIdentifier) AS source
                ON target.ProviderKey = source.ProviderKey AND target.ExternalIdentifier = source.ExternalIdentifier
            WHEN MATCHED THEN
                UPDATE SET LastLogin = SYSUTCDATETIME(), DisplayName = @DisplayName, Email = @Email
            WHEN NOT MATCHED THEN
                INSERT (ProviderKey, ExternalIdentifier, DisplayName, Email, FirstLogin, LastLogin)
                VALUES (@ProviderKey, @ExternalIdentifier, @DisplayName, @Email, SYSUTCDATETIME(), SYSUTCDATETIME())
            OUTPUT inserted.UserKey, inserted.ProviderKey, inserted.ExternalIdentifier,
                   inserted.DisplayName, inserted.Email, inserted.FirstLogin, inserted.LastLogin;
            """;

        var user = await connection.QuerySingleAsync<AppUser>(sql, new
        {
            ProviderKey = providerKey,
            ExternalIdentifier = externalIdentifier,
            DisplayName = displayName,
            Email = email
        });

        return user;
    }

    public async Task<AppUser?> GetByKeyAsync(int userKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = "SELECT * FROM web.app_user WHERE UserKey = @UserKey";
        return await connection.QuerySingleOrDefaultAsync<AppUser>(sql, new { UserKey = userKey });
    }
}
