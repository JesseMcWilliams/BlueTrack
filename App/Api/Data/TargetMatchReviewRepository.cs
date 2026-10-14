using BlueTrack.Api.Errors;
using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>Backs the Target Match Review admin page (Design_Risk-Scoring.md, D-101-105, D-119 Phase B).</summary>
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

        // D-194: a missing review is 404, an already-resolved one 409, a bad resolution 400.
        var row = await connection.QuerySingleOrDefaultAsync<(string IdentifierType, string IdentifierValue, int? CandidateTargetKey, string? Resolution)>(
            "SELECT IdentifierType, IdentifierValue, CandidateTargetKey, Resolution FROM web.target_match_review WITH (UPDLOCK) WHERE TargetMatchReviewKey = @Key",
            new { Key = targetMatchReviewKey }, transaction);
        if (row.IdentifierValue is null)
        {
            throw ApiProblemException.NotFound($"Target match review {targetMatchReviewKey} doesn't exist.");
        }
        if (row.Resolution is not null)
        {
            throw ApiProblemException.Conflict($"This review was already resolved ({row.Resolution}).");
        }

        if (request.Resolution == "Merged")
        {
            var mergeIntoTargetKey = request.MergeIntoTargetKey ?? row.CandidateTargetKey
                ?? throw ApiProblemException.BadRequest("No target to merge into -- choose one.");
            var exists = await connection.QuerySingleOrDefaultAsync<int?>(
                "SELECT 1 FROM web.dim_target WHERE TargetKey = @TargetKey", new { TargetKey = mergeIntoTargetKey }, transaction);
            if (exists is null)
            {
                throw ApiProblemException.BadRequest($"Target {mergeIntoTargetKey} doesn't exist -- choose another.");
            }
            await AssignIdentifierAsync(connection, transaction, mergeIntoTargetKey, row.IdentifierType, row.IdentifierValue);
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
                ?? throw ApiProblemException.BadRequest($"Unknown target type '{newTargetTypeCode}'.");

            var targetKey = await connection.QuerySingleAsync<int>("""
                INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore)
                OUTPUT inserted.TargetKey
                VALUES (@TargetTypeKey, @TargetName, @RiskScore)
                """, new { TargetTypeKey = newTargetTypeKey, TargetName = request.NewTargetName ?? row.IdentifierValue, RiskScore = request.NewTargetRiskScore ?? 0 }, transaction);
            await AssignIdentifierAsync(connection, transaction, targetKey, row.IdentifierType, row.IdentifierValue);
        }
        else if (request.Resolution != "Ignored")
        {
            throw ApiProblemException.BadRequest($"Unknown resolution '{request.Resolution}'.");
        }

        await connection.ExecuteAsync(
            "UPDATE web.target_match_review SET Resolution = @Resolution, ResolvedBy = @ResolvedBy, ResolvedDate = SYSUTCDATETIME() WHERE TargetMatchReviewKey = @Key",
            new { Key = targetMatchReviewKey, request.Resolution, ResolvedBy = resolvedByUserKey }, transaction);

        transaction.Commit();
        return (row.IdentifierType, row.IdentifierValue);
    }

    /// <summary>
    /// D-225: gives the reviewed identifier to <paramref name="targetKey"/>.
    /// A review row's identifier is one an existing Target (the candidate)
    /// already carries, and web.target_identifier is unique per type and
    /// value, so a plain INSERT always failed (409) -- the identifier is
    /// moved instead. Merging into the candidate itself changes nothing.
    /// </summary>
    private static async Task AssignIdentifierAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction,
        int targetKey, string identifierType, string identifierValue)
    {
        var moved = await connection.ExecuteAsync(
            "UPDATE web.target_identifier SET TargetKey = @TargetKey WHERE IdentifierType = @IdentifierType AND IdentifierValue = @IdentifierValue",
            new { TargetKey = targetKey, IdentifierType = identifierType, IdentifierValue = identifierValue }, transaction);
        if (moved == 0)
        {
            await connection.ExecuteAsync(
                "INSERT INTO web.target_identifier (TargetKey, IdentifierType, IdentifierValue) VALUES (@TargetKey, @IdentifierType, @IdentifierValue)",
                new { TargetKey = targetKey, IdentifierType = identifierType, IdentifierValue = identifierValue }, transaction);
        }
    }
}
