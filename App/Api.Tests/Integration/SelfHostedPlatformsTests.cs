using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-244 (36_BlueTrack_SelfHostedPlatformsAndProperties.sql): usp_Load_DimPlatform adds a Self-Hosted platform
/// per distinct PolicyID, and usp_Load_FactAccount gives each Self-Hosted account its PlatformKey, its logon
/// domain (LogonDomain) and, when CPMDisabled holds a reason, AutoManaged = 0 and that reason. The procedure
/// flags every Privilege Cloud / Self-Hosted account missing from staging as deleted in source, so the test
/// saves those flags first and puts them back afterwards, as ImportSkipDeletedAccountsTests does.
/// </summary>
public class SelfHostedPlatformsTests
{
    private static SqlConnection Open() => new(TestDatabase.ConnectionString);

    [Fact]
    public async Task Load_AddsPlatformsFromPolicyIds_AndFillsPlatformLogonDomainAndManualReason()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var batch = Guid.NewGuid();
        var safeId = Random.Shared.NextInt64(900_000_000, 999_999_999);
        var windows = $"WinTest{tag}";
        var unix = $"UnixTest{tag}";
        await using var connection = Open();
        var saved = (await connection.QueryAsync<(long AccountKey, bool IsDeleted, bool IsDeletedInSource)>("""
            SELECT fa.AccountKey, fa.IsDeleted, fa.IsDeletedInSource FROM dbo.fact_account fa
            JOIN dbo.dim_source_system s ON s.SourceSystemKey = fa.SourceSystemKey
            WHERE s.SourceSystemCode IN ('PRIVCLOUD', 'SELFHOSTED')
            """)).ToList();

        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO stg_sh_files (ImportBatchId, SourceFileName, CAFSafeID, CAFFileID, CAFFileName, CAFType, CAFCreationDate)
                VALUES (@batch, 'test', @safeId, 1, 'sh-managed',  2, '2025-01-01'),
                       (@batch, 'test', @safeId, 2, 'sh-disabled', 2, '2025-01-01'),
                       (@batch, 'test', @safeId, 3, 'sh-blank',    2, '2025-01-01'),
                       (@batch, 'test', @safeId, 4, 'sh-nopolicy', 2, '2025-01-01'),
                       (@batch, 'test', @safeId, 5, 'sh-notpassword', 1, '2025-01-01');
                INSERT INTO stg_sh_objectproperties (ImportBatchId, SourceFileName, CAOPSafeId, CAOPFileId, CAOPObjectPropertyName, CAOPObjectPropertyValue)
                VALUES (@batch, 'test', @safeId, 1, 'PolicyID',    @windows),
                       (@batch, 'test', @safeId, 1, 'LogonDomain', ' CORP '),
                       (@batch, 'test', @safeId, 2, 'PolicyID',    @unix),
                       (@batch, 'test', @safeId, 2, 'CPMDisabled', 'Manually managed by the platform team'),
                       (@batch, 'test', @safeId, 3, 'PolicyID',    @windows),
                       (@batch, 'test', @safeId, 3, 'CPMDisabled', '   '),
                       (@batch, 'test', @safeId, 5, 'PolicyID',    'NotAPasswordPolicy' + @tag);
                EXEC usp_Load_DimPlatform;
                EXEC usp_Load_FactAccount;
                """, new { batch, safeId, windows, unix, tag });

            var platforms = (await connection.QueryAsync<(string PlatformID, string? PlatformName, string Source)>("""
                SELECT p.PlatformID, p.PlatformName, s.SourceSystemCode
                FROM dbo.dim_platform p JOIN dbo.dim_source_system s ON s.SourceSystemKey = p.SourceSystemKey
                WHERE p.PlatformID IN (@windows, @unix) OR p.PlatformID = 'NotAPasswordPolicy' + @tag
                """, new { windows, unix, tag })).ToList();
            Assert.Equal(2, platforms.Count);   // the file that isn't a password adds none
            Assert.All(platforms, p => Assert.Equal("SELFHOSTED", p.Source));
            Assert.All(platforms, p => Assert.Equal(p.PlatformID, p.PlatformName));

            async Task<(string? Platform, string? Domain, bool? AutoManaged, string? Reason)> Account(int fileId) =>
                await connection.QuerySingleAsync<(string?, string?, bool?, string?)>("""
                    SELECT p.PlatformID, fa.PlatformLogonDomain, fa.AutoManaged, fa.ManualReason
                    FROM dbo.fact_account fa LEFT JOIN dbo.dim_platform p ON p.PlatformKey = fa.PlatformKey
                    WHERE fa.SourceAccountId = CAST(@safeId AS nvarchar(50)) + '_' + CAST(@fileId AS nvarchar(50))
                    """, new { safeId, fileId });

            Assert.Equal((windows, "CORP", (bool?)null, (string?)null), await Account(1));   // a domain; CPM says nothing
            Assert.Equal((unix, (string?)null, (bool?)false, "Manually managed by the platform team"), await Account(2));
            Assert.Equal((windows, (string?)null, (bool?)null, (string?)null), await Account(3));   // a blank CPMDisabled is no reason
            Assert.Equal(((string?)null, (string?)null, (bool?)null, (string?)null), await Account(4));   // no PolicyID: no platform

            // A second load changes nothing: the platforms are added once.
            await connection.ExecuteAsync("EXEC usp_Load_DimPlatform;");
            Assert.Equal(2, await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM dbo.dim_platform WHERE PlatformID IN (@windows, @unix)", new { windows, unix }));

            // A policy no account uses any more stays: nothing points at it is no reason to remove it.
            await connection.ExecuteAsync("DELETE FROM stg_sh_objectproperties WHERE ImportBatchId = @batch AND CAOPObjectPropertyValue = @unix; EXEC usp_Load_DimPlatform;", new { batch, unix });
            Assert.Equal(1, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.dim_platform WHERE PlatformID = @unix", new { unix }));
        }
        finally
        {
            await connection.ExecuteAsync("""
                DELETE FROM stg_sh_objectproperties WHERE ImportBatchId = @batch;
                DELETE FROM stg_sh_files WHERE ImportBatchId = @batch;
                DELETE pm FROM dbo.platform_account_type_map pm JOIN dbo.dim_platform p ON p.PlatformKey = pm.PlatformKey WHERE p.PlatformID IN (@windows, @unix);
                """, new { batch, windows, unix });
            await connection.ExecuteAsync("""
                DELETE h FROM web.account_deletion_history h JOIN dbo.fact_account fa ON fa.AccountKey = h.AccountKey WHERE fa.SourceAccountId LIKE @shPattern;
                DELETE d FROM web.account_deletion d JOIN dbo.fact_account fa ON fa.AccountKey = d.AccountKey WHERE fa.SourceAccountId LIKE @shPattern;
                DELETE p FROM dbo.fact_account_progress p JOIN dbo.fact_account fa ON fa.AccountKey = p.AccountKey WHERE fa.SourceAccountId LIKE @shPattern;
                DELETE r FROM dbo.account_reconciliation r JOIN dbo.fact_account fa ON fa.AccountKey = r.LegacyAccountKey WHERE fa.SourceAccountId LIKE @shPattern;
                DELETE FROM dbo.fact_account WHERE SourceAccountId LIKE @shPattern;
                DELETE FROM dbo.dim_platform WHERE PlatformID IN (@windows, @unix);
                """, new { shPattern = $"{safeId}[_]%", windows, unix });
            foreach (var row in saved)
            {
                await connection.ExecuteAsync("UPDATE dbo.fact_account SET IsDeleted = @IsDeleted, IsDeletedInSource = @IsDeletedInSource WHERE AccountKey = @AccountKey", row);
            }
        }
    }
}
