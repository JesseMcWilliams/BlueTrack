using Dapper;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the Phase B bulk-CSV import mechanics for the three mapping feeds
/// (Access Group -> Target, Account -> Access Group, direct Account ->
/// Target) plus the shared account/target/access-group resolution helpers
/// every import row needs (Design_Risk_Scoring.md, D-101-105, D-119).
/// </summary>
public sealed class RiskScoringImportRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<long?> ResolveAccountKeyAsync(string? accountKeyText, string? accountName)
    {
        using var connection = connectionFactory.Create();

        if (long.TryParse(accountKeyText, out var accountKey))
        {
            var exists = await connection.QuerySingleOrDefaultAsync<long?>(
                "SELECT AccountKey FROM dbo.fact_account WHERE AccountKey = @AccountKey AND IsDeleted = 0", new { AccountKey = accountKey });
            if (exists is not null)
            {
                return exists;
            }
        }

        if (!string.IsNullOrWhiteSpace(accountName))
        {
            return await connection.QuerySingleOrDefaultAsync<long?>(
                "SELECT TOP 1 AccountKey FROM dbo.fact_account WHERE AccountName = @AccountName AND IsDeleted = 0", new { AccountName = accountName });
        }

        return null;
    }

    public async Task<int?> ResolveAccessGroupKeyAsync(string groupIdentifier)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT AccessGroupKey FROM web.dim_access_group WHERE GroupIdentifier = @GroupIdentifier", new { GroupIdentifier = groupIdentifier });
    }

    /// <summary>Resolution only -- never creates a Target (matches Design_Risk_Scoring.md's PendingSafeDerived/mapping-import rule that a miss here just means no row yet, not an auto-create).</summary>
    public async Task<int?> ResolveTargetKeyByIdentifierAsync(string identifierType, string identifierValue)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT TargetKey FROM web.target_identifier WHERE IdentifierType = @IdentifierType AND IdentifierValue = @IdentifierValue",
            new { IdentifierType = identifierType, IdentifierValue = identifierValue });
    }

    public async Task<int?> UpsertAccessGroupAsync(string groupName, string groupIdentifier, string groupScope, int baseRiskScore, string? description, string? discoverySource, Guid importBatchId, string sourceFileName)
    {
        using var connection = connectionFactory.Create();
        var existingKey = await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT AccessGroupKey FROM web.dim_access_group WHERE GroupIdentifier = @GroupIdentifier", new { GroupIdentifier = groupIdentifier });

        if (existingKey is not null)
        {
            await connection.ExecuteAsync("""
                UPDATE web.dim_access_group
                SET GroupName = @GroupName, GroupScope = @GroupScope, BaseRiskScore = @BaseRiskScore,
                    Description = @Description, DiscoverySource = @DiscoverySource, IsRiskScoreStale = 1,
                    ImportBatchId = @ImportBatchId, SourceFileName = @SourceFileName, ModifiedDate = SYSUTCDATETIME()
                WHERE AccessGroupKey = @AccessGroupKey
                """, new { AccessGroupKey = existingKey, GroupName = groupName, GroupScope = groupScope, BaseRiskScore = baseRiskScore, Description = description, DiscoverySource = discoverySource, ImportBatchId = importBatchId, SourceFileName = sourceFileName });
            return existingKey;
        }

        return await connection.QuerySingleAsync<int>("""
            INSERT INTO web.dim_access_group (GroupName, GroupIdentifier, GroupScope, BaseRiskScore, Description, DiscoverySource, IsRiskScoreStale, ImportBatchId, SourceFileName, ModifiedDate)
            OUTPUT inserted.AccessGroupKey
            VALUES (@GroupName, @GroupIdentifier, @GroupScope, @BaseRiskScore, @Description, @DiscoverySource, 1, @ImportBatchId, @SourceFileName, SYSUTCDATETIME())
            """, new { GroupName = groupName, GroupIdentifier = groupIdentifier, GroupScope = groupScope, BaseRiskScore = baseRiskScore, Description = description, DiscoverySource = discoverySource, ImportBatchId = importBatchId, SourceFileName = sourceFileName });
    }

    public async Task<bool> InsertAccessGroupTargetMapAsync(int accessGroupKey, int targetKey, Guid importBatchId, string sourceFileName)
    {
        using var connection = connectionFactory.Create();
        var alreadyExists = await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT 1 FROM web.access_group_target_map WHERE AccessGroupKey = @AccessGroupKey AND TargetKey = @TargetKey",
            new { AccessGroupKey = accessGroupKey, TargetKey = targetKey });
        if (alreadyExists is not null)
        {
            return false;
        }

        await connection.ExecuteAsync(
            "INSERT INTO web.access_group_target_map (AccessGroupKey, TargetKey, ImportBatchId, SourceFileName) VALUES (@AccessGroupKey, @TargetKey, @ImportBatchId, @SourceFileName)",
            new { AccessGroupKey = accessGroupKey, TargetKey = targetKey, ImportBatchId = importBatchId, SourceFileName = sourceFileName });

        // The group's reachable-target set just changed -- its computed score is now stale.
        await connection.ExecuteAsync("UPDATE web.dim_access_group SET IsRiskScoreStale = 1 WHERE AccessGroupKey = @AccessGroupKey", new { AccessGroupKey = accessGroupKey });
        return true;
    }

    public async Task<bool> InsertAccountAccessGroupMapAsync(long accountKey, int accessGroupKey, Guid importBatchId, string sourceFileName)
    {
        using var connection = connectionFactory.Create();
        var alreadyExists = await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT 1 FROM web.account_access_group_map WHERE AccountKey = @AccountKey AND AccessGroupKey = @AccessGroupKey",
            new { AccountKey = accountKey, AccessGroupKey = accessGroupKey });
        if (alreadyExists is not null)
        {
            return false;
        }

        await connection.ExecuteAsync(
            "INSERT INTO web.account_access_group_map (AccountKey, AccessGroupKey, ImportBatchId, SourceFileName) VALUES (@AccountKey, @AccessGroupKey, @ImportBatchId, @SourceFileName)",
            new { AccountKey = accountKey, AccessGroupKey = accessGroupKey, ImportBatchId = importBatchId, SourceFileName = sourceFileName });

        await MarkAccountRiskStaleAsync(connection, accountKey);
        return true;
    }

    public async Task<bool> InsertAccountTargetMapAsync(long accountKey, int targetKey, string sourceMethod, Guid? importBatchId, string? sourceFileName)
    {
        using var connection = connectionFactory.Create();
        var alreadyExists = await connection.QuerySingleOrDefaultAsync<int?>(
            "SELECT 1 FROM web.account_target_map WHERE AccountKey = @AccountKey AND TargetKey = @TargetKey",
            new { AccountKey = accountKey, TargetKey = targetKey });
        if (alreadyExists is not null)
        {
            return false;
        }

        await connection.ExecuteAsync(
            "INSERT INTO web.account_target_map (AccountKey, TargetKey, SourceMethod, ImportBatchId, SourceFileName) VALUES (@AccountKey, @TargetKey, @SourceMethod, @ImportBatchId, @SourceFileName)",
            new { AccountKey = accountKey, TargetKey = targetKey, SourceMethod = sourceMethod, ImportBatchId = importBatchId, SourceFileName = sourceFileName });

        await MarkAccountRiskStaleAsync(connection, accountKey);
        return true;
    }

    private static async Task MarkAccountRiskStaleAsync(System.Data.IDbConnection connection, long accountKey)
    {
        // web.account_risk_score rows are created lazily -- an account with
        // no row yet simply has no computed score until the next recalculation.
        var exists = await connection.QuerySingleOrDefaultAsync<long?>("SELECT AccountKey FROM web.account_risk_score WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
        if (exists is null)
        {
            await connection.ExecuteAsync("INSERT INTO web.account_risk_score (AccountKey, IsRiskScoreStale) VALUES (@AccountKey, 1)", new { AccountKey = accountKey });
        }
        else
        {
            await connection.ExecuteAsync("UPDATE web.account_risk_score SET IsRiskScoreStale = 1 WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
        }
    }
}
