using Dapper;

namespace BlueTrack.Api.Data;

/// <summary>
/// D-349: Admin > Risk Score, Defaults: the default scores (web.app_config) and each kind of finding's risk value
/// (web.risk_finding_type); and the findings now (web.risk_finding, rebuilt by web.usp_DeriveRiskFindings).
/// </summary>
public sealed class RiskDefaultsRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<RiskDefaults> GetAsync()
    {
        using var connection = connectionFactory.Create();
        using var grid = await connection.QueryMultipleAsync("""
            SELECT TOP (1) DefaultAddressTargetRiskScore, DefaultTargetRiskScore, DefaultAccessGroupRiskScore FROM web.app_config;
            SELECT FindingType, DisplayName, Description, RiskValue, IsEnabled FROM web.risk_finding_type ORDER BY SortOrder, FindingType;
            """);
        var scores = await grid.ReadSingleAsync<(int Address, int Target, int Group)>();
        var types = (await grid.ReadAsync<RiskFindingTypeSetting>()).ToList();
        return new RiskDefaults
        {
            DefaultAddressTargetRiskScore = scores.Address,
            DefaultTargetRiskScore = scores.Target,
            DefaultAccessGroupRiskScore = scores.Group,
            FindingTypes = types
        };
    }

    /// <summary>
    /// Saves the defaults and the finding values. When a finding's value or whether it counts changed, every score is
    /// marked stale, so the next recalculation (Recalculate Now on Reports > Risk Score, or the nightly load) uses it.
    /// A default score applies to targets and groups made from now on; existing ones keep theirs.
    /// </summary>
    public async Task<bool> SaveAsync(SaveRiskDefaultsRequest request)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("""
            UPDATE web.app_config
            SET DefaultAddressTargetRiskScore = @DefaultAddressTargetRiskScore,
                DefaultTargetRiskScore = @DefaultTargetRiskScore,
                DefaultAccessGroupRiskScore = @DefaultAccessGroupRiskScore
            """, request, transaction);

        var changed = 0;
        foreach (var type in request.FindingTypes)
        {
            changed += await connection.ExecuteAsync("""
                UPDATE web.risk_finding_type SET RiskValue = @RiskValue, IsEnabled = @IsEnabled
                WHERE FindingType = @FindingType AND (RiskValue <> @RiskValue OR IsEnabled <> @IsEnabled)
                """, type, transaction);
        }
        if (changed > 0)
            await connection.ExecuteAsync(
                "UPDATE web.dim_access_group SET IsRiskScoreStale = 1; UPDATE web.account_risk_score SET IsRiskScoreStale = 1;", transaction: transaction);
        transaction.Commit();
        return changed > 0;
    }

    public async Task<IReadOnlySet<string>> GetFindingTypeNamesAsync()
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<string>("SELECT FindingType FROM web.risk_finding_type")).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The findings now, one row per kind of finding and target or account, with how many DNA rows gave it. Names only:
    /// the hard-coded credential's file and line stay on Reports > DNA Hard-Coded Credentials (ViewDnaSensitiveColumns).
    /// </summary>
    public async Task<IReadOnlyList<RiskFindingRow>> GetFindingsAsync(int max)
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<RiskFindingRow>("""
            SELECT TOP (@max) f.FindingType, ft.DisplayName AS FindingName, ft.RiskValue, ft.IsEnabled,
                   f.TargetKey, t.TargetName, f.AccountKey, fa.AccountName, fa.UserName, fa.Address, s.SafeName, f.FindingCount
            FROM (SELECT FindingType, TargetKey, AccountKey, COUNT(*) AS FindingCount
                  FROM web.risk_finding GROUP BY FindingType, TargetKey, AccountKey) f
            JOIN web.risk_finding_type ft ON ft.FindingType = f.FindingType
            LEFT JOIN web.dim_target t ON t.TargetKey = f.TargetKey
            LEFT JOIN dbo.fact_account fa ON fa.AccountKey = f.AccountKey
            LEFT JOIN dbo.dim_safe s ON s.SafeKey = fa.SafeKey
            ORDER BY ft.SortOrder, COALESCE(t.TargetName, fa.Address, fa.AccountName), fa.UserName
            """, new { max })).ToList();
    }

    public async Task<int> CountFindingsAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM (SELECT DISTINCT FindingType, TargetKey, AccountKey FROM web.risk_finding) f");
    }

    /// <summary>Rebuilds the findings now, rather than at the next DNA import or recalculation.</summary>
    public async Task DeriveAsync()
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("EXEC web.usp_DeriveRiskFindings", commandTimeout: 600);
    }
}

public sealed class RiskDefaults
{
    public int DefaultAddressTargetRiskScore { get; init; }
    public int DefaultTargetRiskScore { get; init; }
    public int DefaultAccessGroupRiskScore { get; init; }
    public IReadOnlyList<RiskFindingTypeSetting> FindingTypes { get; init; } = [];
}

public sealed class RiskFindingTypeSetting
{
    public string FindingType { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string Description { get; init; } = "";
    public int RiskValue { get; init; }
    public bool IsEnabled { get; init; }
}

public sealed class SaveRiskDefaultsRequest
{
    public int DefaultAddressTargetRiskScore { get; init; }
    public int DefaultTargetRiskScore { get; init; }
    public int DefaultAccessGroupRiskScore { get; init; }
    public IReadOnlyList<SaveRiskFindingType> FindingTypes { get; init; } = [];
}

public sealed class SaveRiskFindingType
{
    public string FindingType { get; init; } = "";
    public int RiskValue { get; init; }
    public bool IsEnabled { get; init; }
}

public sealed class RiskFindingRow
{
    public string FindingType { get; init; } = "";
    public string FindingName { get; init; } = "";
    public int RiskValue { get; init; }
    public bool IsEnabled { get; init; }
    public int? TargetKey { get; init; }
    public string? TargetName { get; init; }
    public long? AccountKey { get; init; }
    public string? AccountName { get; init; }
    public string? UserName { get; init; }
    public string? Address { get; init; }
    public string? SafeName { get; init; }
    public int FindingCount { get; init; }
}
