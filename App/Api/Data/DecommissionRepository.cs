using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// D-186: the decommission / ignored-safe name patterns
/// (02_BlueTrack_Baseline_EtlLoads.sql). All matching runs in SQL
/// (dbo.fn_MatchesNamePattern), so the settings helper, the reports and the
/// nightly import agree exactly.
/// </summary>
public sealed class DecommissionRepository(IDbConnectionFactory connectionFactory)
{
    public static readonly string[] Modes = ["Off", "Prefix", "Suffix", "Regex"];

    public async Task<IReadOnlyList<DecomSafeRow>> GetSafesAsync()
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<DecomSafeRow>(
            "SELECT * FROM web.vw_decom_safe ORDER BY SafeName, SourceSystemName")).AsList();
    }

    public async Task<IReadOnlyList<DecomAccountRow>> GetAccountsAsync()
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<DecomAccountRow>(
            "SELECT * FROM web.vw_decom_account ORDER BY SafeName, UserName, AccountName")).AsList();
    }

    /// <summary>Whether this database's matching function was built with regex (SQL Server 2025, compatibility level 170).</summary>
    public async Task<bool> IsRegexSupportedAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<bool>("SELECT dbo.fn_RegexSupported()");
    }

    /// <summary>Null when the pattern is usable; otherwise why not.</summary>
    public async Task<string?> ValidateAsync(NamePattern pattern, bool regexSupported)
    {
        if (!Modes.Contains(pattern.Mode)) return $"Type must be one of {string.Join(", ", Modes)}.";
        if (pattern.Mode == "Off") return null;
        if (string.IsNullOrWhiteSpace(pattern.Value)) return "Enter a value, or set the type to Off.";
        if (pattern.Value.Length > 200) return "The value must be 200 characters or fewer.";
        if (pattern.Mode != "Regex") return null;
        if (!regexSupported) return "Regex needs SQL Server 2025 (compatibility level 170); use Prefix or Suffix.";
        try
        {
            using var connection = connectionFactory.Create();
            await connection.QuerySingleAsync<bool>("SELECT dbo.fn_MatchesNamePattern(N'', 'Regex', @Value)", new { pattern.Value });
            return null;
        }
        catch (SqlException ex)
        {
            return $"Not a valid regular expression: {ex.Message}";
        }
    }

    /// <summary>Which of the sample names each pattern matches (patterns that fail validation match nothing).</summary>
    public async Task<IReadOnlyList<PatternTestRow>> TestAsync(IReadOnlyList<string> names, NamePattern safeDecom, NamePattern accountDecom, NamePattern safeIgnore)
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<PatternTestRow>("""
            SELECT n.[value] AS Name,
                   CAST(dbo.fn_MatchesNamePattern(n.[value], @SafeDecomMode, @SafeDecomValue) AS BIT) AS SafeDecom,
                   CAST(dbo.fn_MatchesNamePattern(n.[value], @AccountDecomMode, @AccountDecomValue) AS BIT) AS AccountDecom,
                   CAST(dbo.fn_MatchesNamePattern(n.[value], @SafeIgnoreMode, @SafeIgnoreValue) AS BIT) AS SafeIgnore
            FROM OPENJSON(@Names) n
            ORDER BY CAST(n.[key] AS INT)
            """, new
        {
            Names = JsonSerializer.Serialize(names),
            SafeDecomMode = safeDecom.Mode, SafeDecomValue = safeDecom.Value,
            AccountDecomMode = accountDecom.Mode, AccountDecomValue = accountDecom.Value,
            SafeIgnoreMode = safeIgnore.Mode, SafeIgnoreValue = safeIgnore.Value
        })).AsList();
    }
}
