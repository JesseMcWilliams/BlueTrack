using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>Backs the Credentials admin page's LDAP Configuration section (D-116).</summary>
public sealed class LdapConfigRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<LdapConfig> GetAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT lc.LdapConfigKey, lc.IsEnabled, lc.DomainController, lc.SearchBase, lc.UseSsl, lc.CredentialKey, c.CredentialName
            FROM web.ldap_config lc
            LEFT JOIN web.credential c ON c.CredentialKey = lc.CredentialKey
            """;
        return await connection.QuerySingleAsync<LdapConfig>(sql);
    }

    public async Task SaveAsync(SaveLdapConfigRequest request, int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            UPDATE web.ldap_config
            SET IsEnabled = @IsEnabled, DomainController = @DomainController, SearchBase = @SearchBase,
                UseSsl = @UseSsl, CredentialKey = @CredentialKey, ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
            """;
        await connection.ExecuteAsync(sql, new
        {
            request.IsEnabled,
            request.DomainController,
            request.SearchBase,
            request.UseSsl,
            request.CredentialKey,
            ModifiedBy = modifiedByUserKey
        });
    }
}
