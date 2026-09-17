using System.Data;
using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// web.discovered_account / web.discovered_account_access_group_map (AD
/// Account Discovery feature, 2026-09-16). Backs both
/// AdAccountDiscoveryService's writes (upsert-by-domain+SamAccountName,
/// replace-all-on-save for matched Access Groups -- same convention
/// TargetRepository already uses for a Target's identifiers) and the new
/// read-only Discovered Accounts report.
/// </summary>
public sealed class DiscoveredAccountRepository(IDbConnectionFactory connectionFactory)
{
    /// <summary>
    /// Best-effort "is this AD account already onboarded" match against
    /// dbo.fact_account.UserName/PlatformLogonDomain -- inherently fuzzy
    /// (fact_account has no stored AD objectSid), so this is used to flag a
    /// plausible match for human review (DiscoveredAccountCandidate.
    /// PossibleExistingAccountKey), never to silently exclude a candidate.
    /// Covers the three common logon-name shapes: bare username, DOMAIN\
    /// username, and username@domain.
    /// </summary>
    public async Task<long?> FindPossibleExistingAccountKeyAsync(string domainName, string samAccountName)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT TOP 1 AccountKey
            FROM dbo.fact_account
            WHERE IsDeleted = 0
              AND UserName IS NOT NULL
              AND (
                    LOWER(UserName) = LOWER(@SamAccountName)
                 OR LOWER(UserName) = LOWER(@DomainName + '\' + @SamAccountName)
                 OR LOWER(UserName) = LOWER(@SamAccountName + '@' + @DomainName)
                 OR (LOWER(PlatformLogonDomain) = LOWER(@DomainName) AND LOWER(UserName) = LOWER(@SamAccountName))
              )
            """;
        return await connection.QuerySingleOrDefaultAsync<long?>(sql, new { DomainName = domainName, SamAccountName = samAccountName });
    }

    /// <summary>Database/32's usp_CalculateRiskScoreForAccessGroupSet, the AccessGroupSet variant of the existing engine (Database/23) that never needs an AccountKey/fact_account row.</summary>
    public async Task<int> CalculateRiskScoreAsync(IReadOnlyList<int> accessGroupKeys)
    {
        using var connection = connectionFactory.Create();
        var table = new DataTable();
        table.Columns.Add("AccessGroupKey", typeof(int));
        foreach (var key in accessGroupKeys)
        {
            table.Rows.Add(key);
        }

        var parameters = new DynamicParameters();
        parameters.Add("AccessGroupKeys", table.AsTableValuedParameter("web.AccessGroupKeyList"));
        parameters.Add("Score", dbType: DbType.Int32, direction: ParameterDirection.Output);
        await connection.ExecuteAsync("usp_CalculateRiskScoreForAccessGroupSet", parameters, commandType: CommandType.StoredProcedure);
        return parameters.Get<int>("Score");
    }

    /// <summary>Insert-or-update by (DomainName, SamAccountName) -- a re-run refreshes LastSeenDate/risk score for an existing candidate rather than duplicating it.</summary>
    public async Task<int> UpsertCandidateAsync(DiscoveredAccountCandidate candidate, int? computedRiskScore)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            MERGE web.discovered_account AS tgt
            USING (SELECT @DomainName AS DomainName, @SamAccountName AS SamAccountName) AS src
            ON tgt.DomainName = src.DomainName AND tgt.SamAccountName = src.SamAccountName
            WHEN MATCHED THEN
                UPDATE SET DistinguishedName = @DistinguishedName, ObjectSid = @ObjectSid, DisplayName = @DisplayName,
                    IsEnabled = @IsEnabled, ComputedRiskScore = @ComputedRiskScore, RiskScoreCalculatedDate = SYSUTCDATETIME(),
                    LastSeenDate = SYSUTCDATETIME(), PossibleExistingAccountKey = @PossibleExistingAccountKey
            WHEN NOT MATCHED THEN
                INSERT (DomainName, SamAccountName, DistinguishedName, ObjectSid, DisplayName, IsEnabled,
                        ComputedRiskScore, RiskScoreCalculatedDate, DiscoveredDate, LastSeenDate, PossibleExistingAccountKey)
                VALUES (@DomainName, @SamAccountName, @DistinguishedName, @ObjectSid, @DisplayName, @IsEnabled,
                        @ComputedRiskScore, SYSUTCDATETIME(), SYSUTCDATETIME(), SYSUTCDATETIME(), @PossibleExistingAccountKey)
            OUTPUT INSERTED.DiscoveredAccountKey;
            """;
        return await connection.QuerySingleAsync<int>(sql, new
        {
            candidate.DomainName,
            candidate.SamAccountName,
            candidate.DistinguishedName,
            candidate.ObjectSid,
            candidate.DisplayName,
            candidate.IsEnabled,
            ComputedRiskScore = computedRiskScore,
            candidate.PossibleExistingAccountKey
        });
    }

    /// <summary>Replace-all-on-save -- mirrors TargetRepository's own convention for a Target's identifiers (small nested collection, no incremental diffing).</summary>
    public async Task ReplaceAccessGroupMappingsAsync(int discoveredAccountKey, IReadOnlyList<int> accessGroupKeys)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync(
            "DELETE FROM web.discovered_account_access_group_map WHERE DiscoveredAccountKey = @DiscoveredAccountKey",
            new { DiscoveredAccountKey = discoveredAccountKey }, transaction);

        if (accessGroupKeys.Count > 0)
        {
            await connection.ExecuteAsync(
                "INSERT INTO web.discovered_account_access_group_map (DiscoveredAccountKey, AccessGroupKey) VALUES (@DiscoveredAccountKey, @AccessGroupKey)",
                accessGroupKeys.Select(key => new { DiscoveredAccountKey = discoveredAccountKey, AccessGroupKey = key }), transaction);
        }

        transaction.Commit();
    }

    /// <summary>Backs the Discovered Accounts report -- every candidate, sortable, with its Risk Band and (when set) the possible-existing-account's name resolved.</summary>
    public async Task<IReadOnlyList<DiscoveredAccount>> GetListAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT
                da.DiscoveredAccountKey, da.DomainName, da.SamAccountName, da.DistinguishedName, da.ObjectSid,
                da.DisplayName, da.IsEnabled, da.ComputedRiskScore, da.RiskScoreCalculatedDate,
                da.DiscoveredDate, da.LastSeenDate, da.PossibleExistingAccountKey,
                fa.AccountName AS PossibleExistingAccountName,
                band.BandName AS RiskScoreBandName
            FROM web.discovered_account da
            LEFT JOIN dbo.fact_account fa ON fa.AccountKey = da.PossibleExistingAccountKey
            LEFT JOIN web.dim_risk_score_band band ON da.ComputedRiskScore BETWEEN band.MinScore AND band.MaxScore
            ORDER BY da.ComputedRiskScore DESC
            """;
        var rows = await connection.QueryAsync<DiscoveredAccount>(sql);
        return rows.AsList();
    }

    /// <summary>Every AccessGroupKey a discovered account matched -- App/Api/AdDiscovery reads dim_access_group directly for the candidate list; this is what the report needs to show which groups drove the score.</summary>
    public async Task<IReadOnlyList<(int AccessGroupKey, string GroupName)>> GetMatchedAccessGroupsAsync(int discoveredAccountKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT ag.AccessGroupKey, ag.GroupName
            FROM web.discovered_account_access_group_map m
            JOIN web.dim_access_group ag ON ag.AccessGroupKey = m.AccessGroupKey
            WHERE m.DiscoveredAccountKey = @DiscoveredAccountKey
            ORDER BY ag.GroupName
            """;
        var rows = await connection.QueryAsync<(int, string)>(sql, new { DiscoveredAccountKey = discoveredAccountKey });
        return rows.AsList();
    }
}
