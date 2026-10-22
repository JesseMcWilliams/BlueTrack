using Dapper;

namespace BlueTrack.Api.Data;

public sealed record PlatformMappingRow(
    int PlatformKey, string SourceSystemName, string PlatformId, string? PlatformName, int? AccountTypeKey, string? AccountTypeName,
    int? SorKey, string? SourceOfRecord, string? Notes, bool IsUnmapped, int AccountCount, int AccountsWithoutType, int AccountsWithoutSor,
    string? PlatformType)
{
    /// <summary>D-293: set by <see cref="PlatformMappingSuggester"/> on unmapped, non-group platforms.</summary>
    public PlatformMappingSuggestion? Suggestion { get; init; }
}

public sealed record PlatformMappingOptions(IReadOnlyList<PlatformMappingChoice> AccountTypes, IReadOnlyList<PlatformMappingChoice> SourcesOfRecord);

public sealed record PlatformMappingChoice(int Key, string Name);

public sealed record PlatformMappingSaveResult(int AccountsTypeFilled, int AccountsSorFilled);

/// <summary>
/// D-247: Admin > Platform Mapping. platform_account_type_map says which account type and source of record an
/// account on a platform gets by default; a new account is given them when it is first tracked
/// (usp_Load_FactAccountProgress), and "fill existing" gives them to accounts already tracked that have none.
/// </summary>
public sealed class PlatformMappingRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<PlatformMappingRow>> ListAsync()
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<PlatformMappingRow>("""
            SELECT p.PlatformKey, ss.SourceSystemName, p.PlatformID AS PlatformId, p.PlatformName,
                   pm.AccountTypeKey, at.AccountTypeName, pm.SORKey AS SorKey, sor.SORName AS SourceOfRecord, pm.Notes,
                   CAST(CASE WHEN pm.PlatformKey IS NULL THEN 1 ELSE 0 END AS BIT) AS IsUnmapped,
                   counts.AccountCount, counts.AccountsWithoutType, counts.AccountsWithoutSor, p.PlatformType
            FROM dbo.dim_platform p
            JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = p.SourceSystemKey
            LEFT JOIN dbo.platform_account_type_map pm ON pm.PlatformKey = p.PlatformKey
            LEFT JOIN dbo.dim_account_type at ON at.AccountTypeKey = pm.AccountTypeKey
            LEFT JOIN dbo.dim_source_of_record sor ON sor.SORKey = pm.SORKey
            OUTER APPLY (
                SELECT COUNT(*) AS AccountCount,
                       COALESCE(SUM(CASE WHEN fap.AccountTypeKey IS NULL THEN 1 ELSE 0 END), 0) AS AccountsWithoutType,
                       COALESCE(SUM(CASE WHEN fap.SORKey IS NULL THEN 1 ELSE 0 END), 0) AS AccountsWithoutSor
                FROM dbo.fact_account fa
                LEFT JOIN dbo.fact_account_progress fap ON fap.AccountKey = fa.AccountKey
                WHERE fa.PlatformKey = p.PlatformKey AND fa.IsDeleted = 0
            ) counts
            ORDER BY CASE WHEN pm.PlatformKey IS NULL THEN 0 ELSE 1 END, counts.AccountCount DESC, ss.SourceSystemName, p.PlatformID
            """);
        return PlatformMappingSuggester.Suggest(rows.AsList(), await GetOptionsAsync());
    }

    public async Task<PlatformMappingOptions> GetOptionsAsync()
    {
        using var connection = connectionFactory.Create();
        var types = await connection.QueryAsync<PlatformMappingChoice>("SELECT AccountTypeKey AS [Key], AccountTypeName AS Name FROM dbo.dim_account_type ORDER BY AccountTypeName");
        var sors = await connection.QueryAsync<PlatformMappingChoice>("SELECT SORKey AS [Key], SORName AS Name FROM dbo.dim_source_of_record ORDER BY SORName");
        return new PlatformMappingOptions(types.AsList(), sors.AsList());
    }

    public async Task<PlatformMappingRow?> GetAsync(int platformKey) => (await ListAsync()).FirstOrDefault(r => r.PlatformKey == platformKey);

    /// <summary>Sets the mapping. With <paramref name="fillExisting"/>, tracked accounts on the platform that have no account type (or no source of record) get these; accounts that already have one are never changed.</summary>
    public async Task<PlatformMappingSaveResult> SaveAsync(int platformKey, int accountTypeKey, int? sorKey, string? notes, bool fillExisting)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("""
            MERGE dbo.platform_account_type_map AS target
            USING (SELECT @platformKey AS PlatformKey) AS source ON target.PlatformKey = source.PlatformKey
            WHEN MATCHED THEN UPDATE SET AccountTypeKey = @accountTypeKey, SORKey = @sorKey, Notes = @notes
            WHEN NOT MATCHED THEN INSERT (PlatformKey, AccountTypeKey, SORKey, Notes) VALUES (@platformKey, @accountTypeKey, @sorKey, @notes);
            """, new { platformKey, accountTypeKey, sorKey, notes }, transaction);

        int typeFilled = 0, sorFilled = 0;
        if (fillExisting)
        {
            typeFilled = await connection.ExecuteAsync("""
                UPDATE fap SET AccountTypeKey = @accountTypeKey, LastUpdated = SYSUTCDATETIME()
                FROM dbo.fact_account_progress fap JOIN dbo.fact_account fa ON fa.AccountKey = fap.AccountKey
                WHERE fa.PlatformKey = @platformKey AND fa.IsDeleted = 0 AND fap.AccountTypeKey IS NULL
                """, new { platformKey, accountTypeKey }, transaction);
            if (sorKey is not null)
            {
                sorFilled = await connection.ExecuteAsync("""
                    UPDATE fap SET SORKey = @sorKey, LastUpdated = SYSUTCDATETIME()
                    FROM dbo.fact_account_progress fap JOIN dbo.fact_account fa ON fa.AccountKey = fap.AccountKey
                    WHERE fa.PlatformKey = @platformKey AND fa.IsDeleted = 0 AND fap.SORKey IS NULL
                    """, new { platformKey, sorKey }, transaction);
            }
        }
        transaction.Commit();
        return new PlatformMappingSaveResult(typeFilled, sorFilled);
    }

    public async Task<bool> RemoveAsync(int platformKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync("DELETE FROM dbo.platform_account_type_map WHERE PlatformKey = @platformKey", new { platformKey }) > 0;
    }
}
