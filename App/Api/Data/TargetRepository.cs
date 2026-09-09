using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the new Targets admin page (Design_Risk_Scoring.md, D-101-105,
/// Phase A). A Target's identifiers (web.target_identifier) are managed as
/// a nested child collection -- replace-all-on-save (delete then re-insert
/// every identifier row for a Target on each update), the simplest correct
/// approach for a small per-Target set, matching how CredentialRepository
/// treats a credential's vault fields as one atomic unit rather than
/// diffing individual field changes.
/// </summary>
public sealed class TargetRepository(IDbConnectionFactory connectionFactory)
{
    private const string SelectSql = """
        SELECT t.TargetKey, t.TargetType, t.TargetName, t.InternalGuid, t.ApplicationKey,
               a.ApplicationName, t.RiskScore, t.Description, t.DiscoverySource, t.ModifiedDate
        FROM web.dim_target t
        LEFT JOIN web.dim_application a ON a.ApplicationKey = t.ApplicationKey
        """;

    public async Task<IReadOnlyList<TargetSummary>> GetAllAsync()
    {
        using var connection = connectionFactory.Create();
        var targets = (await connection.QueryAsync<TargetSummary>($"{SelectSql} ORDER BY t.TargetName")).ToList();
        if (targets.Count == 0)
        {
            return targets;
        }

        var identifierRows = await connection.QueryAsync<(int TargetKey, string IdentifierType, string IdentifierValue)>(
            "SELECT TargetKey, IdentifierType, IdentifierValue FROM web.target_identifier ORDER BY IdentifierType");
        var identifiersByTarget = identifierRows
            .GroupBy(r => r.TargetKey)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<TargetIdentifier>)g.Select(r => new TargetIdentifier { IdentifierType = r.IdentifierType, IdentifierValue = r.IdentifierValue }).ToList());

        return targets.Select(t => new TargetSummary
        {
            TargetKey = t.TargetKey,
            TargetType = t.TargetType,
            TargetName = t.TargetName,
            InternalGuid = t.InternalGuid,
            ApplicationKey = t.ApplicationKey,
            ApplicationName = t.ApplicationName,
            RiskScore = t.RiskScore,
            Description = t.Description,
            DiscoverySource = t.DiscoverySource,
            ModifiedDate = t.ModifiedDate,
            Identifiers = identifiersByTarget.GetValueOrDefault(t.TargetKey, [])
        }).ToList();
    }

    public async Task<IReadOnlyList<TargetIdentifierTypeSummary>> GetIdentifierTypesAsync()
    {
        using var connection = connectionFactory.Create();
        var rows = await connection.QueryAsync<TargetIdentifierTypeSummary>(
            "SELECT IdentifierType, MatchPriority, RequiresReview FROM web.dim_target_identifier_type ORDER BY MatchPriority");
        return rows.AsList();
    }

    public async Task<int> CreateAsync(SaveTargetRequest request, int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var targetKey = await connection.QuerySingleAsync<int>("""
            INSERT INTO web.dim_target (TargetType, TargetName, ApplicationKey, RiskScore, Description, DiscoverySource, CreatedBy, ModifiedBy, ModifiedDate)
            OUTPUT inserted.TargetKey
            VALUES (@TargetType, @TargetName, @ApplicationKey, @RiskScore, @Description, @DiscoverySource, @ModifiedBy, @ModifiedBy, SYSUTCDATETIME())
            """, new { request.TargetType, request.TargetName, request.ApplicationKey, request.RiskScore, request.Description, request.DiscoverySource, ModifiedBy = modifiedByUserKey }, transaction);

        await InsertIdentifiersAsync(connection, transaction, targetKey, request.Identifiers);

        transaction.Commit();
        return targetKey;
    }

    public async Task UpdateAsync(int targetKey, SaveTargetRequest request, int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync("""
            UPDATE web.dim_target
            SET TargetType = @TargetType, TargetName = @TargetName, ApplicationKey = @ApplicationKey,
                RiskScore = @RiskScore, Description = @Description, DiscoverySource = @DiscoverySource,
                ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
            WHERE TargetKey = @TargetKey
            """, new { TargetKey = targetKey, request.TargetType, request.TargetName, request.ApplicationKey, request.RiskScore, request.Description, request.DiscoverySource, ModifiedBy = modifiedByUserKey }, transaction);

        await connection.ExecuteAsync("DELETE FROM web.target_identifier WHERE TargetKey = @TargetKey", new { TargetKey = targetKey }, transaction);
        await InsertIdentifiersAsync(connection, transaction, targetKey, request.Identifiers);

        transaction.Commit();
    }

    public async Task DeleteAsync(int targetKey)
    {
        using var connection = connectionFactory.Create();
        // web.target_identifier has no ON DELETE CASCADE -- delete children first,
        // same explicit-cascade convention RoleRepository.DeleteRoleAsync already uses.
        connection.Open();
        using var transaction = connection.BeginTransaction();
        await connection.ExecuteAsync("DELETE FROM web.target_identifier WHERE TargetKey = @TargetKey", new { TargetKey = targetKey }, transaction);
        await connection.ExecuteAsync("DELETE FROM web.dim_target WHERE TargetKey = @TargetKey", new { TargetKey = targetKey }, transaction);
        transaction.Commit();
    }

    private static async Task InsertIdentifiersAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, int targetKey, IReadOnlyList<SaveTargetIdentifierRequest> identifiers)
    {
        if (identifiers.Count == 0)
        {
            return;
        }

        const string sql = "INSERT INTO web.target_identifier (TargetKey, IdentifierType, IdentifierValue) VALUES (@TargetKey, @IdentifierType, @IdentifierValue)";
        foreach (var identifier in identifiers)
        {
            await connection.ExecuteAsync(sql, new { TargetKey = targetKey, identifier.IdentifierType, identifier.IdentifierValue }, transaction);
        }
    }
}
