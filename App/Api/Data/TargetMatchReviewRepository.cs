using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>Backs the Target Match Review admin page (Design_Risk_Scoring.md, D-101-105, D-119 Phase B).</summary>
public sealed class TargetMatchReviewRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<TargetMatchReviewSummary>> GetPendingAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT r.TargetMatchReviewKey, r.CandidateTargetKey, t.TargetName AS CandidateTargetName,
                   r.IdentifierType, r.IdentifierValue, r.SourceFileName, r.CreatedDate, r.Resolution
            FROM web.target_match_review r
            LEFT JOIN web.dim_target t ON t.TargetKey = r.CandidateTargetKey
            WHERE r.Resolution IS NULL
            ORDER BY r.CreatedDate
            """;
        var rows = await connection.QueryAsync<TargetMatchReviewSummary>(sql);
        return rows.AsList();
    }

    public async Task<(string IdentifierType, string IdentifierValue)> ResolveAsync(int targetMatchReviewKey, ResolveTargetMatchReviewRequest request, int? resolvedByUserKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        var row = await connection.QuerySingleAsync<(string IdentifierType, string IdentifierValue, int? CandidateTargetKey)>(
            "SELECT IdentifierType, IdentifierValue, CandidateTargetKey FROM web.target_match_review WHERE TargetMatchReviewKey = @Key",
            new { Key = targetMatchReviewKey }, transaction);

        if (request.Resolution == "Merged")
        {
            var mergeIntoTargetKey = request.MergeIntoTargetKey ?? row.CandidateTargetKey
                ?? throw new InvalidOperationException("No target to merge into -- supply MergeIntoTargetKey.");
            await connection.ExecuteAsync(
                "INSERT INTO web.target_identifier (TargetKey, IdentifierType, IdentifierValue) VALUES (@TargetKey, @IdentifierType, @IdentifierValue)",
                new { TargetKey = mergeIntoTargetKey, row.IdentifierType, row.IdentifierValue }, transaction);
        }
        else if (request.Resolution == "NewTarget")
        {
            // D-124 Phase 2: TargetType is now an FK (TargetTypeKey) -- resolve
            // the requested type code (or the 'Other' default) against
            // web.dim_target_type by TypeCode rather than assuming a specific
            // IDENTITY value, since that's only guaranteed stable as a code.
            var newTargetTypeCode = request.NewTargetType ?? "Other";
            var newTargetTypeKey = await connection.QuerySingleOrDefaultAsync<int?>(
                "SELECT TargetTypeKey FROM web.dim_target_type WHERE TypeCode = @TypeCode",
                new { TypeCode = newTargetTypeCode }, transaction)
                ?? throw new InvalidOperationException($"Unknown target type '{newTargetTypeCode}'.");

            var targetKey = await connection.QuerySingleAsync<int>("""
                INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore)
                OUTPUT inserted.TargetKey
                VALUES (@TargetTypeKey, @TargetName, @RiskScore)
                """, new { TargetTypeKey = newTargetTypeKey, TargetName = request.NewTargetName ?? row.IdentifierValue, RiskScore = request.NewTargetRiskScore ?? 0 }, transaction);
            await connection.ExecuteAsync(
                "INSERT INTO web.target_identifier (TargetKey, IdentifierType, IdentifierValue) VALUES (@TargetKey, @IdentifierType, @IdentifierValue)",
                new { TargetKey = targetKey, row.IdentifierType, row.IdentifierValue }, transaction);
        }
        else if (request.Resolution != "Ignored")
        {
            throw new InvalidOperationException($"Unknown resolution '{request.Resolution}'.");
        }

        await connection.ExecuteAsync(
            "UPDATE web.target_match_review SET Resolution = @Resolution, ResolvedBy = @ResolvedBy, ResolvedDate = SYSUTCDATETIME() WHERE TargetMatchReviewKey = @Key",
            new { Key = targetMatchReviewKey, request.Resolution, ResolvedBy = resolvedByUserKey }, transaction);

        transaction.Commit();
        return (row.IdentifierType, row.IdentifierValue);
    }
}
