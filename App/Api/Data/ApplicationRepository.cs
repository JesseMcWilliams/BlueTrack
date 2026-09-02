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

    public async Task<IReadOnlyList<ApplicationDetail>> GetAllDetailedAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT ApplicationKey, ApplicationGUID, ApplicationCode, ApplicationName, Description,
                   OwnerName, OwnerEmail, TechnicalName, TechnicalEmail, Notes
            FROM web.dim_application
            ORDER BY ApplicationName
            """;
        var rows = await connection.QueryAsync<ApplicationDetail>(sql);
        return rows.AsList();
    }

    public async Task<int> CreateAsync(SaveApplicationRequest request)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            INSERT INTO web.dim_application
                (ApplicationCode, ApplicationName, Description, OwnerName, OwnerEmail, TechnicalName, TechnicalEmail, Notes)
            OUTPUT inserted.ApplicationKey
            VALUES (@ApplicationCode, @ApplicationName, @Description, @OwnerName, @OwnerEmail, @TechnicalName, @TechnicalEmail, @Notes)
            """;
        return await connection.QuerySingleAsync<int>(sql, request);
    }

    public async Task UpdateAsync(int applicationKey, SaveApplicationRequest request)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            UPDATE web.dim_application
            SET ApplicationCode = @ApplicationCode, ApplicationName = @ApplicationName, Description = @Description,
                OwnerName = @OwnerName, OwnerEmail = @OwnerEmail, TechnicalName = @TechnicalName,
                TechnicalEmail = @TechnicalEmail, Notes = @Notes
            WHERE ApplicationKey = @ApplicationKey
            """;
        await connection.ExecuteAsync(sql, new
        {
            ApplicationKey = applicationKey,
            request.ApplicationCode,
            request.ApplicationName,
            request.Description,
            request.OwnerName,
            request.OwnerEmail,
            request.TechnicalName,
            request.TechnicalEmail,
            request.Notes
        });
    }

    public async Task<IReadOnlyList<SafeSummary>> GetAllSafesAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT s.SafeKey, s.SafeName, s.ApplicationKey, a.ApplicationName
            FROM dbo.dim_safe s
            LEFT JOIN web.dim_application a ON a.ApplicationKey = s.ApplicationKey
            ORDER BY s.SafeName
            """;
        var rows = await connection.QueryAsync<SafeSummary>(sql);
        return rows.AsList();
    }

    public async Task AssignSafeApplicationAsync(int safeKey, int? applicationKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = "UPDATE dbo.dim_safe SET ApplicationKey = @ApplicationKey WHERE SafeKey = @SafeKey";
        await connection.ExecuteAsync(sql, new { SafeKey = safeKey, ApplicationKey = applicationKey });
    }
}
