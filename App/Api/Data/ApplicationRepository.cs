using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the application-scoping dropdown on the Risk Exception create/edit
/// form. web.dim_application is a small, curated business list (D-31), not
/// a bulk import table, so loading it in full is fine -- unlike accounts.
/// </summary>
public sealed class ApplicationRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<ApplicationSummary>> GetAllAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT ApplicationKey, ApplicationCode, ApplicationName
            FROM web.dim_application
            ORDER BY ApplicationName
            """;
        var rows = await connection.QueryAsync<ApplicationSummary>(sql);
        return rows.AsList();
    }
}
