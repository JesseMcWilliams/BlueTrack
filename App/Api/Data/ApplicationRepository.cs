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

    /// <summary>Every application by name; one page of them when <paramref name="page"/> is given (D-213).</summary>
    public async Task<IReadOnlyList<ApplicationDetail>> GetAllDetailedAsync(int? page = null, int? pageSize = null)
    {
        using var connection = connectionFactory.Create();
        var (normalizedPage, normalizedPageSize) = PagingParams.Normalize(page, pageSize);
        var sql = $"""
            SELECT a.ApplicationKey, a.ApplicationGUID, a.ApplicationCode, a.ApplicationName, a.Description,
                   a.OwnerName, a.OwnerEmail, a.TechnicalName, a.TechnicalEmail, a.Notes,
                   (SELECT COUNT(*) FROM dbo.dim_safe s WHERE s.ApplicationKey = a.ApplicationKey) AS SafeCount,
                   (SELECT COUNT(*) FROM dbo.fact_account_progress p WHERE p.ApplicationKey = a.ApplicationKey) AS AccountCount,
                   (SELECT COUNT(*) FROM web.dim_target t WHERE t.ApplicationKey = a.ApplicationKey) AS TargetCount,
                   (SELECT COUNT(*) FROM web.risk_exception r WHERE r.ApplicationKey = a.ApplicationKey) AS ExceptionCount
            FROM web.dim_application a
            ORDER BY a.ApplicationName, a.ApplicationKey
            {(page is null ? "" : "OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY")}
            """;
        var rows = await connection.QueryAsync<ApplicationDetail>(sql,
            new { Offset = PagingParams.Offset(normalizedPage, normalizedPageSize), PageSize = normalizedPageSize });
        return rows.AsList();
    }

    /// <summary>
    /// UI review W8-5: deletes an application nothing uses. Returns null when it doesn't exist, and otherwise the
    /// counts of what still uses it (all zero = deleted); a non-zero count means nothing was deleted.
    /// </summary>
    public async Task<(string Name, int Safes, int Accounts, int Targets, int Exceptions)?> DeleteIfUnusedAsync(int applicationKey)
    {
        using var connection = connectionFactory.Create();
        var row = await connection.QuerySingleOrDefaultAsync<(string Name, int Safes, int Accounts, int Targets, int Exceptions)?>("""
            SELECT a.ApplicationName AS Name,
                   (SELECT COUNT(*) FROM dbo.dim_safe s WHERE s.ApplicationKey = a.ApplicationKey) AS Safes,
                   (SELECT COUNT(*) FROM dbo.fact_account_progress p WHERE p.ApplicationKey = a.ApplicationKey) AS Accounts,
                   (SELECT COUNT(*) FROM web.dim_target t WHERE t.ApplicationKey = a.ApplicationKey) AS Targets,
                   (SELECT COUNT(*) FROM web.risk_exception r WHERE r.ApplicationKey = a.ApplicationKey) AS Exceptions
            FROM web.dim_application a WHERE a.ApplicationKey = @ApplicationKey
            """, new { ApplicationKey = applicationKey });
        if (row is null) return null;
        var counts = row.Value;
        if (counts.Safes + counts.Accounts + counts.Targets + counts.Exceptions > 0) return counts;
        await connection.ExecuteAsync("DELETE FROM web.dim_application WHERE ApplicationKey = @ApplicationKey", new { ApplicationKey = applicationKey });
        return counts;
    }

    public async Task<int> GetCountAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.dim_application");
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

    /// <summary>Every safe by name; one page of them when <paramref name="page"/> is given (D-213).</summary>
    public async Task<IReadOnlyList<SafeSummary>> GetAllSafesAsync(int? page = null, int? pageSize = null, string? search = null, bool unassignedOnly = false)
    {
        using var connection = connectionFactory.Create();
        var (normalizedPage, normalizedPageSize) = PagingParams.Normalize(page, pageSize);
        // UI review W8-2: the Safes table can be searched by name and narrowed to Safes with no application.
        var sql = $"""
            SELECT s.SafeKey, s.SafeName, s.ApplicationKey, a.ApplicationName
            FROM dbo.dim_safe s
            LEFT JOIN web.dim_application a ON a.ApplicationKey = s.ApplicationKey
            WHERE {SafeFilterSql}
            ORDER BY s.SafeName, s.SafeKey
            {(page is null ? "" : "OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY")}
            """;
        var rows = await connection.QueryAsync<SafeSummary>(sql,
            new { Offset = PagingParams.Offset(normalizedPage, normalizedPageSize), PageSize = normalizedPageSize, SearchPattern = ToContainsPattern(search), Unassigned = unassignedOnly });
        return rows.AsList();
    }

    private const string SafeFilterSql = "(@SearchPattern IS NULL OR s.SafeName LIKE @SearchPattern) AND (@Unassigned = 0 OR s.ApplicationKey IS NULL)";

    /// <summary>Safe names often contain '_' (and '['), which LIKE reads as wildcards, so the text is matched literally.</summary>
    private static string? ToContainsPattern(string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return null;
        var escaped = search.Trim().Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
        return $"%{escaped}%";
    }

    public async Task<int> GetSafeCountAsync() => await CountSafesAsync(null, false);

    /// <summary>UI review W8-2: how many Safes match the search and the unassigned filter (ignoring paging).</summary>
    public async Task<int> CountSafesAsync(string? search, bool unassignedOnly)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>($"SELECT COUNT(*) FROM dbo.dim_safe s WHERE {SafeFilterSql}",
            new { SearchPattern = ToContainsPattern(search), Unassigned = unassignedOnly });
    }

    /// <summary>D-180: every safe with its source, for the assignments CSV import's name matching.</summary>
    public async Task<IReadOnlyList<SafeForMatching>> GetSafesForMatchingAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT s.SafeKey, s.SafeName, s.SourceSystemKey, ss.SourceSystemName, s.ApplicationKey
            FROM dbo.dim_safe s
            JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = s.SourceSystemKey
            """;
        var rows = await connection.QueryAsync<SafeForMatching>(sql);
        return rows.AsList();
    }

    /// <summary>
    /// D-239: active accounts whose username and address match exactly
    /// (ignoring case and surrounding spaces; both are required, as in D-183),
    /// with the source they come from and their own application.
    /// </summary>
    public async Task<IReadOnlyList<AccountForMatching>> FindAccountsForAssignmentAsync(string userName, string address)
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<AccountForMatching>("""
            SELECT fa.AccountKey, fa.AccountName, ss.SourceSystemName, fap.ApplicationKey,
                   CAST(CASE WHEN fap.AccountKey IS NULL THEN 0 ELSE 1 END AS BIT) AS HasProgress
            FROM dbo.fact_account fa
            JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = fa.SourceSystemKey
            LEFT JOIN dbo.fact_account_progress fap ON fap.AccountKey = fa.AccountKey
            WHERE fa.IsDeleted = 0 AND UPPER(LTRIM(RTRIM(fa.UserName))) = UPPER(@userName)
              AND UPPER(LTRIM(RTRIM(fa.Address))) = UPPER(@address)
            """, new { userName = userName.Trim(), address = address.Trim() })).AsList();
    }

    /// <summary>D-239: sets or clears an account's own application on its progress row.</summary>
    public async Task AssignAccountApplicationAsync(long accountKey, int? applicationKey)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(
            "UPDATE dbo.fact_account_progress SET ApplicationKey = @ApplicationKey, LastUpdated = SYSUTCDATETIME() WHERE AccountKey = @AccountKey",
            new { AccountKey = accountKey, ApplicationKey = applicationKey });
    }

    public async Task AssignSafeApplicationAsync(int safeKey, int? applicationKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = "UPDATE dbo.dim_safe SET ApplicationKey = @ApplicationKey WHERE SafeKey = @SafeKey";
        await connection.ExecuteAsync(sql, new { SafeKey = safeKey, ApplicationKey = applicationKey });
    }
}
