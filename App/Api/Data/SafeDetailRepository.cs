using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>D-308: one Safe's page. Its accounts come from the Accounts list's own query, filtered by Safe.</summary>
public sealed class SafeDetailRepository(IDbConnectionFactory connectionFactory)
{
    /// <summary>Null when there's no such Safe.</summary>
    public async Task<SafeDetail?> GetAsync(int safeKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<SafeDetail>("""
            SELECT ds.SafeKey, ds.SafeName, ds.Description, ss.SourceSystemName, ds.ManagingCPM, CAST(ds.CreatedDate AS DATETIME2) AS CreatedDate,
                   ds.ApplicationKey, da.ApplicationCode, da.ApplicationName, da.OwnerName AS ApplicationOwnerName,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM web.vw_decom_safe v WHERE v.SafeKey = ds.SafeKey) THEN 1 ELSE 0 END AS BIT) AS IsDecomFlagged,
                   CAST(dbo.fn_MatchesNamePattern(ds.SafeName, ac.SafeIgnoreMode, ac.SafeIgnoreValue) AS BIT) AS IsIgnored,
                   CAST(CASE WHEN EXISTS (SELECT 1 FROM dbo.vw_selfhosted_internal_safe iis WHERE iis.SafeKey = ds.SafeKey) THEN 1 ELSE 0 END AS BIT) AS IsInternal,
                   (SELECT COUNT(*) FROM dbo.fact_account fa WHERE fa.SafeKey = ds.SafeKey AND fa.IsDeleted = 0) AS ActiveAccountCount,
                   (SELECT COUNT(*) FROM dbo.fact_account fa WHERE fa.SafeKey = ds.SafeKey AND fa.IsDeleted = 1) AS DeletedAccountCount
            FROM dbo.dim_safe ds
            JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = ds.SourceSystemKey
            LEFT JOIN web.dim_application da ON da.ApplicationKey = ds.ApplicationKey
            CROSS JOIN web.app_config ac
            WHERE ds.SafeKey = @safeKey
            """, new { safeKey });
    }
}
