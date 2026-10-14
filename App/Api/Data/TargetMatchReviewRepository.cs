using BlueTrack.Api.Errors;
using BlueTrack.Api.RiskScoring;
using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the Target Match Review admin page (Design_Risk-Scoring.md, D-101-105,
/// D-119 Phase B). D-229: a review is resolved as Linked (the incoming row
/// becomes its own target, linked to the primary), NewTarget (its own,
/// unlinked target) or Ignored (remembered by TargetMatchingService). The
/// reviewed identifier always stays where it is.
/// </summary>
public sealed class TargetMatchReviewRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<TargetMatchReviewSummary>> GetPendingAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT r.TargetMatchReviewKey, r.CandidateTargetKey, t.TargetName AS CandidateTargetName,
                   r.IdentifierType, r.IdentifierValue, r.SourceFileName, r.CreatedDate, r.Resolution,
                   r.TargetName, dtt.DisplayName AS TargetTypeDisplayName, r.RiskScore, r.Description, r.DiscoverySource
            FROM web.target_match_review r
            LEFT JOIN web.dim_target t ON t.TargetKey = r.CandidateTargetKey
            LEFT JOIN web.dim_target_type dtt ON dtt.TargetTypeKey = r.TargetTypeKey
            WHERE r.Resolution IS NULL
            ORDER BY r.CreatedDate, r.TargetMatchReviewKey
            """;
        var rows = (await connection.QueryAsync<TargetMatchReviewSummary>(sql)).AsList();
        if (rows.Count == 0)
        {
            return rows;
        }

        var identifierRows = await connection.QueryAsync<(int Key, string IdentifierType, string IdentifierValue)>("""
            SELECT i.TargetMatchReviewKey, i.IdentifierType, i.IdentifierValue
            FROM web.target_match_review_identifier i
            JOIN web.target_match_review r ON r.TargetMatchReviewKey = i.TargetMatchReviewKey
            JOIN web.dim_target_identifier_type dt ON dt.IdentifierType = i.IdentifierType
            WHERE r.Resolution IS NULL
            ORDER BY dt.MatchPriority, i.IdentifierType, i.IdentifierValue
            """);
        var byReview = identifierRows.GroupBy(r => r.Key).ToDictionary(g => g.Key,
            g => (IReadOnlyList<TargetIdentifier>)g.Select(r => new TargetIdentifier { IdentifierType = r.IdentifierType, IdentifierValue = r.IdentifierValue }).ToList());

        return rows.Select(r => new TargetMatchReviewSummary
        {
            TargetMatchReviewKey = r.TargetMatchReviewKey,
            CandidateTargetKey = r.CandidateTargetKey,
            CandidateTargetName = r.CandidateTargetName,
            IdentifierType = r.IdentifierType,
            IdentifierValue = r.IdentifierValue,
            SourceFileName = r.SourceFileName,
            CreatedDate = r.CreatedDate,
            Resolution = r.Resolution,
            TargetName = r.TargetName,
            TargetTypeDisplayName = r.TargetTypeDisplayName,
            RiskScore = r.RiskScore,
            Description = r.Description,
            DiscoverySource = r.DiscoverySource,
            Identifiers = byReview.GetValueOrDefault(r.TargetMatchReviewKey, [])
        }).ToList();
    }

    private sealed class TargetRow
    {
        public int TargetKey { get; init; }
        public required string TargetName { get; init; }
    }

    private sealed class ReviewRow
    {
        public required string IdentifierType { get; init; }
        public required string IdentifierValue { get; init; }
        public int? CandidateTargetKey { get; init; }
        public string? Resolution { get; init; }
        public string? TargetName { get; init; }
        public int? TargetTypeKey { get; init; }
        public int? RiskScore { get; init; }
        public string? Description { get; init; }
        public string? DiscoverySource { get; init; }
        public Guid? ImportBatchId { get; init; }
        public string? SourceFileName { get; init; }
    }

    public async Task<TargetMatchReviewResolution> ResolveAsync(int targetMatchReviewKey, ResolveTargetMatchReviewRequest request, int? resolvedByUserKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        // D-194: a missing review is 404, an already-resolved one 409, a bad resolution 400.
        var row = await connection.QuerySingleOrDefaultAsync<ReviewRow>("""
            SELECT IdentifierType, IdentifierValue, CandidateTargetKey, Resolution,
                   TargetName, TargetTypeKey, RiskScore, Description, DiscoverySource, ImportBatchId, SourceFileName
            FROM web.target_match_review WITH (UPDLOCK) WHERE TargetMatchReviewKey = @Key
            """, new { Key = targetMatchReviewKey }, transaction)
            ?? throw ApiProblemException.NotFound($"Target match review {targetMatchReviewKey} doesn't exist.");
        if (row.Resolution is not null)
        {
            throw ApiProblemException.Conflict($"This review was already resolved ({row.Resolution}).");
        }

        int? targetKey = null;
        string? targetName = null;
        int? primaryTargetKey = null;
        string? primaryTargetName = null;

        if (request.Resolution is "Linked" or "NewTarget")
        {
            if (request.Resolution == "Linked")
            {
                var chosenKey = request.PrimaryTargetKey ?? row.CandidateTargetKey
                    ?? throw ApiProblemException.BadRequest("No target to link to -- choose one.");
                // A linked target's primary is the one to link to: links are one level deep.
                var primary = await connection.QuerySingleOrDefaultAsync<TargetRow>("""
                    SELECT p.TargetKey, p.TargetName
                    FROM web.dim_target t
                    LEFT JOIN web.vw_linked_target lt ON lt.TargetKey = t.TargetKey
                    JOIN web.dim_target p ON p.TargetKey = COALESCE(lt.PrimaryTargetKey, t.TargetKey)
                    WHERE t.TargetKey = @TargetKey
                    """, new { TargetKey = chosenKey }, transaction)
                    ?? throw ApiProblemException.BadRequest($"Target {chosenKey} doesn't exist -- choose another.");
                (primaryTargetKey, primaryTargetName) = (primary.TargetKey, primary.TargetName);
            }

            // D-124 Phase 2: the type is an FK; a requested type is resolved by its TypeCode.
            int targetTypeKey;
            if (request.NewTargetType is { } typeCode)
            {
                targetTypeKey = await connection.QuerySingleOrDefaultAsync<int?>(
                    "SELECT TargetTypeKey FROM web.dim_target_type WHERE TypeCode = @TypeCode", new { TypeCode = typeCode }, transaction)
                    ?? throw ApiProblemException.BadRequest($"Unknown target type '{typeCode}'.");
            }
            else
            {
                targetTypeKey = row.TargetTypeKey ?? await connection.QuerySingleAsync<int>(
                    "SELECT TargetTypeKey FROM web.dim_target_type WHERE TypeCode = 'Other'", transaction: transaction);
            }

            var riskScore = request.NewTargetRiskScore ?? row.RiskScore ?? 0;
            if (riskScore is < 0 or > 1000)
            {
                throw ApiProblemException.BadRequest("Risk Score must be between 0 and 1000.");
            }

            // A review queued before D-229 has no stored name: the analyst's, else the identifier value.
            targetName = string.IsNullOrWhiteSpace(request.NewTargetName) ? row.TargetName ?? row.IdentifierValue : request.NewTargetName.Trim();
            targetKey = await connection.QuerySingleAsync<int>("""
                INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore, Description, DiscoverySource, ImportBatchId, SourceFileName, CreatedBy, ModifiedBy, ModifiedDate)
                OUTPUT inserted.TargetKey
                VALUES (@TargetTypeKey, @TargetName, @RiskScore, @Description, @DiscoverySource, @ImportBatchId, @SourceFileName, @UserKey, @UserKey, SYSUTCDATETIME())
                """, new
                {
                    TargetTypeKey = targetTypeKey, TargetName = targetName, RiskScore = riskScore, row.Description, row.DiscoverySource,
                    row.ImportBatchId, row.SourceFileName, UserKey = resolvedByUserKey
                }, transaction);

            // The incoming row's other identifiers that no target has yet; the reviewed one stays where it is.
            var identifiers = (await connection.QueryAsync<(string IdentifierType, string IdentifierValue)>(
                    "SELECT IdentifierType, IdentifierValue FROM web.target_match_review_identifier WHERE TargetMatchReviewKey = @Key",
                    new { Key = targetMatchReviewKey }, transaction))
                .Where(i => !(i.IdentifierType == row.IdentifierType && i.IdentifierValue == row.IdentifierValue))
                .Select(i => new TargetMatchIdentifier(i.IdentifierType, i.IdentifierValue));
            await TargetMatchingService.AddMissingIdentifiersAsync(connection, transaction, targetKey.Value, identifiers);

            if (primaryTargetKey is { } primaryKey)
            {
                await connection.ExecuteAsync("""
                    INSERT INTO web.target_link (LinkedTargetKey, PrimaryTargetKey, LinkMethod, TargetMatchReviewKey, LinkedBy)
                    VALUES (@LinkedTargetKey, @PrimaryTargetKey, 'MatchReview', @ReviewKey, @UserKey)
                    """, new { LinkedTargetKey = targetKey, PrimaryTargetKey = primaryKey, ReviewKey = targetMatchReviewKey, UserKey = resolvedByUserKey }, transaction);
                // Whatever reaches the new target now scores at the primary's risk.
                await RiskScoreStalenessPropagator.MarkEntitiesReachingTargetStaleAsync(connection, transaction, targetKey.Value);
            }
        }
        else if (request.Resolution == "Merged")
        {
            throw ApiProblemException.BadRequest("'Merged' is no longer a resolution: use 'Linked' (D-229).");
        }
        else if (request.Resolution != "Ignored")
        {
            throw ApiProblemException.BadRequest($"Unknown resolution '{request.Resolution}'.");
        }

        await connection.ExecuteAsync("""
            UPDATE web.target_match_review
            SET Resolution = @Resolution, ResolvedBy = @ResolvedBy, ResolvedDate = SYSUTCDATETIME(), ResolvedTargetKey = @ResolvedTargetKey
            WHERE TargetMatchReviewKey = @Key
            """, new { Key = targetMatchReviewKey, request.Resolution, ResolvedBy = resolvedByUserKey, ResolvedTargetKey = targetKey }, transaction);

        transaction.Commit();
        return new TargetMatchReviewResolution(row.IdentifierType, row.IdentifierValue, request.Resolution,
            targetKey, targetName, primaryTargetKey, primaryTargetName);
    }
}
