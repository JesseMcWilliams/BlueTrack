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

    /// <summary>
    /// Requested directly (2026-09-16): the workflow for moving a discovered
    /// account into onboarding tracking. Inserts (or finds an existing) real
    /// dbo.fact_account row under the 'DISCOVERY' source (already present in
    /// dim_source_system -- confirmed directly, just never wired to a load
    /// procedure before now), keyed on ObjectSid -- a stable, already-known
    /// unique value, giving this natural idempotency: re-accepting an
    /// already-accepted candidate is a no-op, not a duplicate-key error.
    /// Creates its fact_account_progress row synchronously (Stage
    /// "Discovered" / Status "Not Started", mirroring
    /// usp_Load_FactAccountProgress's own shape -- AccountTypeKey/SORKey
    /// left NULL, same as any account with no PlatformKey yet) rather than
    /// waiting for the next nightly Load, so it shows up in the Account
    /// Progress list immediately. Requires
    /// Database/36_BlueTrack_FixAutoAdvanceForDiscoveredAccounts.sql to
    /// already be applied -- without it, the next nightly Load would wrongly
    /// auto-promote this account straight to "Onboarded to Vault" (see that
    /// script's own header for the full explanation).
    /// </summary>
    public async Task<long> AcceptAsync(int discoveredAccountKey, int reviewedByUserKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var candidate = await connection.QuerySingleOrDefaultAsync<(string DomainName, string SamAccountName, string? DisplayName, string? ObjectSid, string Status, long? ResolvedAccountKey)>(
            "SELECT DomainName, SamAccountName, DisplayName, ObjectSid, Status, ResolvedAccountKey FROM web.discovered_account WHERE DiscoveredAccountKey = @DiscoveredAccountKey",
            new { DiscoveredAccountKey = discoveredAccountKey }, transaction);

        if (candidate.Status == "Accepted" && candidate.ResolvedAccountKey is not null)
        {
            transaction.Commit();
            return candidate.ResolvedAccountKey.Value;
        }

        if (string.IsNullOrWhiteSpace(candidate.ObjectSid))
        {
            throw new InvalidOperationException($"Discovered account {discoveredAccountKey} has no ObjectSid recorded -- cannot accept without a stable unique identifier.");
        }

        var accountName = string.IsNullOrWhiteSpace(candidate.DisplayName)
            ? $"{candidate.SamAccountName} ({candidate.DomainName})"
            : $"{candidate.DisplayName} ({candidate.DomainName})";

        const string acceptSql = """
            DECLARE @DiscoveryKey INT = (SELECT SourceSystemKey FROM dbo.dim_source_system WHERE SourceSystemCode = 'DISCOVERY');
            DECLARE @DiscoveredStageKey INT = (SELECT StageKey FROM dbo.dim_blueprint_stage WHERE StageName = 'Discovered');
            DECLARE @NotStartedStatusKey INT = (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = 'Not Started');
            DECLARE @AccountKey BIGINT;

            SELECT @AccountKey = AccountKey FROM dbo.fact_account WHERE SourceSystemKey = @DiscoveryKey AND SourceAccountId = @ObjectSid;

            IF @AccountKey IS NULL
            BEGIN
                INSERT INTO dbo.fact_account (SourceSystemKey, SourceAccountId, AccountName, UserName, PlatformLogonDomain, IsDeleted, CreatedDate)
                VALUES (@DiscoveryKey, @ObjectSid, @AccountName, @SamAccountName, @DomainName, 0, CAST(SYSUTCDATETIME() AS DATE));
                SET @AccountKey = SCOPE_IDENTITY();
            END

            IF NOT EXISTS (SELECT 1 FROM dbo.fact_account_progress WHERE AccountKey = @AccountKey)
            BEGIN
                INSERT INTO dbo.fact_account_progress (AccountKey, CurrentStageKey, CurrentStatusKey)
                VALUES (@AccountKey, @DiscoveredStageKey, @NotStartedStatusKey);
            END

            SELECT @AccountKey;
            """;

        var accountKey = await connection.QuerySingleAsync<long>(acceptSql, new
        {
            candidate.ObjectSid,
            AccountName = accountName,
            candidate.SamAccountName,
            candidate.DomainName
        }, transaction);

        await connection.ExecuteAsync(
            """
            UPDATE web.discovered_account
            SET Status = 'Accepted', ResolvedAccountKey = @AccountKey, ReviewedBy = @ReviewedByUserKey, ReviewedDate = SYSUTCDATETIME()
            WHERE DiscoveredAccountKey = @DiscoveredAccountKey
            """,
            new { AccountKey = accountKey, ReviewedByUserKey = reviewedByUserKey, DiscoveredAccountKey = discoveredAccountKey }, transaction);

        transaction.Commit();
        return accountKey;
    }

    /// <summary>Marks a candidate resolved with no dbo.fact_account write -- false positives, or the already-onboarded case (PossibleExistingAccountKey already flags that for the reviewer).</summary>
    public async Task DismissAsync(int discoveredAccountKey, int reviewedByUserKey)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync(
            """
            UPDATE web.discovered_account
            SET Status = 'Dismissed', ReviewedBy = @ReviewedByUserKey, ReviewedDate = SYSUTCDATETIME()
            WHERE DiscoveredAccountKey = @DiscoveredAccountKey
            """,
            new { ReviewedByUserKey = reviewedByUserKey, DiscoveredAccountKey = discoveredAccountKey });
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

    private static readonly IReadOnlyDictionary<string, string> SortableColumns = new Dictionary<string, string>
    {
        ["domainName"] = "da.DomainName",
        ["samAccountName"] = "da.SamAccountName",
        ["computedRiskScore"] = "da.ComputedRiskScore",
        ["discoveredDate"] = "da.DiscoveredDate",
        ["lastSeenDate"] = "da.LastSeenDate"
    };

    private const string SelectSql = """
        SELECT
            da.DiscoveredAccountKey, da.DomainName, da.SamAccountName, da.DistinguishedName, da.ObjectSid,
            da.DisplayName, da.IsEnabled, da.ComputedRiskScore, da.RiskScoreCalculatedDate,
            da.DiscoveredDate, da.LastSeenDate, da.PossibleExistingAccountKey,
            fa.AccountName AS PossibleExistingAccountName,
            band.BandName AS RiskScoreBandName,
            da.Status, da.ResolvedAccountKey, da.ReviewedBy, da.ReviewedDate
        FROM web.discovered_account da
        LEFT JOIN dbo.fact_account fa ON fa.AccountKey = da.PossibleExistingAccountKey
        LEFT JOIN web.dim_risk_score_band band ON da.ComputedRiskScore BETWEEN band.MinScore AND band.MaxScore
        """;

    /// <summary>Backs the Discovered Accounts report -- defaults to Status='New' only (Accepted/Dismissed rows stay in the table for audit history, not deleted, just filtered out of the default view); pass a null status to see every row regardless of status, for a possible future audit view. D-124 Phase 3-style paging.</summary>
    public async Task<IReadOnlyList<DiscoveredAccount>> GetListAsync(
        IReadOnlyList<(string Field, bool Descending)>? sortBy = null,
        int? page = null,
        int? pageSize = null,
        string? status = "New")
    {
        using var connection = connectionFactory.Create();
        var (normalizedPage, normalizedPageSize) = PagingParams.Normalize(page, pageSize);
        var sql = $"""
            {SelectSql}
            WHERE (@Status IS NULL OR da.Status = @Status)
            ORDER BY {BuildOrderByClause(sortBy)}
            OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY
            """;
        var rows = await connection.QueryAsync<DiscoveredAccount>(sql, new
        {
            Status = status,
            Offset = PagingParams.Offset(normalizedPage, normalizedPageSize),
            PageSize = normalizedPageSize
        });
        return rows.AsList();
    }

    /// <summary>Grand total regardless of Status, matching this app's own GetTotalCountAsync convention elsewhere (e.g. AccessGroupRepository) -- GetFilteredCountAsync below is what actually matches the default Status='New' view.</summary>
    public async Task<int> GetTotalCountAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.discovered_account");
    }

    public async Task<int> GetFilteredCountAsync(string? status = "New")
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM web.discovered_account WHERE (@Status IS NULL OR Status = @Status)", new { Status = status });
    }

    private static string BuildOrderByClause(IReadOnlyList<(string Field, bool Descending)>? sortBy)
    {
        if (sortBy is not { Count: > 0 })
        {
            return "da.ComputedRiskScore DESC";
        }

        var clauses = sortBy
            .Where(s => SortableColumns.ContainsKey(s.Field))
            .Select(s => $"{SortableColumns[s.Field]} {(s.Descending ? "DESC" : "ASC")}")
            .ToList();

        return clauses.Count > 0 ? string.Join(", ", clauses) : "da.ComputedRiskScore DESC";
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
