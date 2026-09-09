using Dapper;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.RiskScoring;

public enum TargetMatchOutcome { Created, Merged, PendingReview }

public sealed record TargetMatchResult(TargetMatchOutcome Outcome, int? TargetKey);

public sealed record TargetMatchIdentifier(string IdentifierType, string IdentifierValue);

/// <summary>
/// Design_Risk_Scoring.md's "Target Matching" section (D-101-105, D-119
/// Phase B) -- the priority-ordered identifier-matching/dedup logic used by
/// both the CSV-upload import path and the internal PendingSafeDerived
/// mechanism (Phase C). The design doc proposed this as a T-SQL stored
/// procedure (usp_MatchOrCreateTarget); implemented here as a C# service
/// instead -- this app had zero existing precedent anywhere for calling a
/// stored procedure from the API layer (confirmed by direct search before
/// building this), while the plain-Dapper-repository pattern used
/// everywhere else in this app already handles everything this logic
/// actually needs (parameterized lookups, a transaction, simple inserts).
/// Same priority-ordered matching, auto-merge, and review-queue behavior
/// either way -- an implementation detail, not a behavior change.
/// </summary>
public sealed class TargetMatchingService(IDbConnectionFactory connectionFactory)
{
    private sealed class CandidateMatch
    {
        public int TargetKey { get; init; }
        public required string IdentifierType { get; init; }
        public required string IdentifierValue { get; init; }
        public int MatchPriority { get; init; }
        public bool RequiresReview { get; init; }
    }

    public async Task<TargetMatchResult> MatchOrCreateAsync(
        string targetType,
        string targetName,
        int riskScore,
        string? description,
        string? discoverySource,
        IReadOnlyList<TargetMatchIdentifier> identifiers,
        Guid? importBatchId,
        string? sourceFileName,
        int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        // Step 1: find the best (lowest MatchPriority number = strongest
        // signal) existing match across every identifier this row supplied.
        CandidateMatch? best = null;
        foreach (var identifier in identifiers)
        {
            var match = await connection.QuerySingleOrDefaultAsync<CandidateMatch>("""
                SELECT TOP 1 ti.TargetKey, ti.IdentifierType, ti.IdentifierValue, dt.MatchPriority, dt.RequiresReview
                FROM web.target_identifier ti
                JOIN web.dim_target_identifier_type dt ON dt.IdentifierType = ti.IdentifierType
                WHERE ti.IdentifierType = @IdentifierType AND ti.IdentifierValue = @IdentifierValue
                """, new { identifier.IdentifierType, identifier.IdentifierValue }, transaction);

            if (match is not null && (best is null || match.MatchPriority < best.MatchPriority))
            {
                best = match;
            }
        }

        // Step 2: no match at all -> new Target + every supplied identifier.
        if (best is null)
        {
            var targetKey = await connection.QuerySingleAsync<int>("""
                INSERT INTO web.dim_target (TargetType, TargetName, RiskScore, Description, DiscoverySource, ImportBatchId, SourceFileName, ModifiedBy, ModifiedDate)
                OUTPUT inserted.TargetKey
                VALUES (@TargetType, @TargetName, @RiskScore, @Description, @DiscoverySource, @ImportBatchId, @SourceFileName, @ModifiedBy, SYSUTCDATETIME())
                """, new { TargetType = targetType, TargetName = targetName, RiskScore = riskScore, Description = description, DiscoverySource = discoverySource, ImportBatchId = importBatchId, SourceFileName = sourceFileName, ModifiedBy = modifiedByUserKey }, transaction);

            foreach (var identifier in identifiers)
            {
                await connection.ExecuteAsync(
                    "INSERT INTO web.target_identifier (TargetKey, IdentifierType, IdentifierValue) VALUES (@TargetKey, @IdentifierType, @IdentifierValue)",
                    new { TargetKey = targetKey, identifier.IdentifierType, identifier.IdentifierValue }, transaction);
            }

            transaction.Commit();
            return new TargetMatchResult(TargetMatchOutcome.Created, targetKey);
        }

        // Step 3: a strong (RequiresReview = 0) match -> auto-merge. Add any
        // identifier values this row supplied that the existing Target
        // doesn't already carry.
        if (!best.RequiresReview)
        {
            foreach (var identifier in identifiers)
            {
                var alreadyExists = await connection.QuerySingleOrDefaultAsync<int?>(
                    "SELECT TargetIdentifierKey FROM web.target_identifier WHERE IdentifierType = @IdentifierType AND IdentifierValue = @IdentifierValue",
                    new { identifier.IdentifierType, identifier.IdentifierValue }, transaction);
                if (alreadyExists is null)
                {
                    await connection.ExecuteAsync(
                        "INSERT INTO web.target_identifier (TargetKey, IdentifierType, IdentifierValue) VALUES (@TargetKey, @IdentifierType, @IdentifierValue)",
                        new { TargetKey = best.TargetKey, identifier.IdentifierType, identifier.IdentifierValue }, transaction);
                }
            }

            transaction.Commit();
            return new TargetMatchResult(TargetMatchOutcome.Merged, best.TargetKey);
        }

        // Step 4: the only match found is weak (e.g. IP-only) -- don't
        // auto-merge, route to the review queue instead.
        await connection.ExecuteAsync("""
            INSERT INTO web.target_match_review (CandidateTargetKey, IdentifierType, IdentifierValue, ImportBatchId, SourceFileName)
            VALUES (@CandidateTargetKey, @IdentifierType, @IdentifierValue, @ImportBatchId, @SourceFileName)
            """, new { CandidateTargetKey = best.TargetKey, best.IdentifierType, best.IdentifierValue, ImportBatchId = importBatchId, SourceFileName = sourceFileName }, transaction);

        transaction.Commit();
        return new TargetMatchResult(TargetMatchOutcome.PendingReview, null);
    }
}
