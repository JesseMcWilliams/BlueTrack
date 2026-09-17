using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the Credentials admin page's LDAP Configuration section (D-116) --
/// extended 2026-09-16 from a singleton to a real per-domain list, for the
/// AD Account Discovery feature's multi-domain needs. GetAllAsync/GetByKeyAsync
/// replace the old bare GetAsync(); LdapGroupMemberResolver and the new
/// discovery job both loop over every enabled row rather than assuming one.
/// </summary>
public sealed class LdapConfigRepository(IDbConnectionFactory connectionFactory)
{
    private const string SelectSql = """
        SELECT lc.LdapConfigKey, lc.DomainName, lc.IsEnabled, lc.DomainController, lc.SearchBase, lc.UseSsl,
               lc.UseTrustedConnection, lc.CredentialKey, c.CredentialName
        FROM web.ldap_config lc
        LEFT JOIN web.credential c ON c.CredentialKey = lc.CredentialKey
        """;

    public async Task<IReadOnlyList<LdapConfig>> GetAllAsync()
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<LdapConfig>($"{SelectSql} ORDER BY lc.DomainName");
        return rows.AsList();
    }

    /// <summary>Every enabled domain config -- what the discovery job/notification LDAP resolution actually iterate over.</summary>
    public async Task<IReadOnlyList<LdapConfig>> GetEnabledAsync()
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<LdapConfig>($"{SelectSql} WHERE lc.IsEnabled = 1 ORDER BY lc.DomainName");
        return rows.AsList();
    }

    public async Task<LdapConfig?> GetByKeyAsync(int ldapConfigKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<LdapConfig>(
            $"{SelectSql} WHERE lc.LdapConfigKey = @LdapConfigKey", new { LdapConfigKey = ldapConfigKey });
    }

    public async Task<int> CreateAsync(SaveLdapConfigRequest request, int? createdByUserKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            INSERT INTO web.ldap_config (DomainName, IsEnabled, DomainController, SearchBase, UseSsl, UseTrustedConnection, CredentialKey, ModifiedBy, ModifiedDate)
            OUTPUT INSERTED.LdapConfigKey
            VALUES (@DomainName, @IsEnabled, @DomainController, @SearchBase, @UseSsl, @UseTrustedConnection, @CredentialKey, @ModifiedBy, SYSUTCDATETIME())
            """;
        return await connection.QuerySingleAsync<int>(sql, new
        {
            request.DomainName,
            request.IsEnabled,
            request.DomainController,
            request.SearchBase,
            request.UseSsl,
            request.UseTrustedConnection,
            request.CredentialKey,
            ModifiedBy = createdByUserKey
        });
    }

    public async Task UpdateAsync(int ldapConfigKey, SaveLdapConfigRequest request, int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            UPDATE web.ldap_config
            SET DomainName = @DomainName, IsEnabled = @IsEnabled, DomainController = @DomainController, SearchBase = @SearchBase,
                UseSsl = @UseSsl, UseTrustedConnection = @UseTrustedConnection, CredentialKey = @CredentialKey,
                ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
            WHERE LdapConfigKey = @LdapConfigKey
            """;
        await connection.ExecuteAsync(sql, new
        {
            LdapConfigKey = ldapConfigKey,
            request.DomainName,
            request.IsEnabled,
            request.DomainController,
            request.SearchBase,
            request.UseSsl,
            request.UseTrustedConnection,
            request.CredentialKey,
            ModifiedBy = modifiedByUserKey
        });
    }

    public async Task DeleteAsync(int ldapConfigKey)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("DELETE FROM web.ldap_config WHERE LdapConfigKey = @LdapConfigKey", new { LdapConfigKey = ldapConfigKey });
    }
}
