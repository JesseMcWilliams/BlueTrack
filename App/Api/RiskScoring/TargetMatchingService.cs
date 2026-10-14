using Dapper;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.RiskScoring;

/// <summary>Ignored (D-229): the same row was ignored on Target Match Review before, so it was skipped.</summary>
public enum TargetMatchOutcome { Created, Merged, PendingReview, Ignored }

public sealed record TargetMatchResult(TargetMatchOutcome Outcome, int? TargetKey);

public sealed record TargetMatchIdentifier(string IdentifierType, string IdentifierValue);

/// <summary>
/// Design_Risk-Scoring.md's "Target Matching" section (D-101-105, D-119
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
        int targetTypeKey,
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
                INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore, Description, DiscoverySource, ImportBatchId, SourceFileName, ModifiedBy, ModifiedDate)
                OUTPUT inserted.TargetKey
                VALUES (@TargetTypeKey, @TargetName, @RiskScore, @Description, @DiscoverySource, @ImportBatchId, @SourceFileName, @ModifiedBy, SYSUTCDATETIME())
                """, new { TargetTypeKey = targetTypeKey, TargetName = targetName, RiskScore = riskScore, Description = description, DiscoverySource = discoverySource, ImportBatchId = importBatchId, SourceFileName = sourceFileName, ModifiedBy = modifiedByUserKey }, transaction);

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
        // identifier values this row supplied that no Target carries yet
        // (an IP address its primary has stays there when the match is a
        // linked target, D-229). Nothing else on the Target changes.
        if (!best.RequiresReview)
        {
            await AddMissingIdentifiersAsync(connection, transaction, best.TargetKey, identifiers);
            transaction.Commit();
            return new TargetMatchResult(TargetMatchOutcome.Merged, best.TargetKey);
        }

        // Step 4: the only match found is weak (e.g. IP-only). D-229: "the
        // same row" is the same discovery source and target name (ignoring
        // case and surrounding spaces) with that identifier.
        var nameKey = targetName.Trim(' ').ToLowerInvariant();
        var sourceKey = (discoverySource ?? "").Trim(' ').ToLowerInvariant();

        // 4a: it was linked before (a linked target from that source with
        // that name), or a review of it was resolved as Link or New Target
        // (that target, even if it has been unlinked since -- never relinked)
        // -> it is that target.
        var knownTargetKey = await connection.QuerySingleOrDefaultAsync<int?>("""
            SELECT TOP (1) TargetKey FROM (
                SELECT lt.TargetKey, CASE WHEN lt.PrimaryTargetKey = @CandidateTargetKey THEN 0 ELSE 1 END AS Rank1, 0 AS Rank2
                FROM web.vw_linked_target lt
                JOIN web.dim_target t ON t.TargetKey = lt.TargetKey
                WHERE LOWER(LTRIM(RTRIM(t.TargetName))) = @NameKey
                  AND LOWER(LTRIM(RTRIM(ISNULL(t.DiscoverySource, N'')))) = @SourceKey
                UNION ALL
                SELECT r.ResolvedTargetKey, 2, r.TargetMatchReviewKey
                FROM web.target_match_review r
                WHERE r.Resolution IN ('Linked', 'NewTarget') AND r.ResolvedTargetKey IS NOT NULL
                  AND r.IdentifierType = @IdentifierType AND r.IdentifierValue = @IdentifierValue
                  AND r.NameMatchKey = @NameKey AND r.SourceMatchKey = @SourceKey
            ) k
            ORDER BY Rank1, Rank2 DESC, TargetKey
            """, new { CandidateTargetKey = best.TargetKey, NameKey = nameKey, SourceKey = sourceKey, best.IdentifierType, best.IdentifierValue }, transaction);
        if (knownTargetKey is { } knownKey)
        {
            await AddMissingIdentifiersAsync(connection, transaction, knownKey, identifiers);
            transaction.Commit();
            return new TargetMatchResult(TargetMatchOutcome.Merged, knownKey);
        }

        // 4b: an analyst ignored it before -> skip it.
        var ignored = await connection.QuerySingleOrDefaultAsync<int?>("""
            SELECT TOP (1) 1 FROM web.target_match_review
            WHERE Resolution = 'Ignored' AND IdentifierType = @IdentifierType AND IdentifierValue = @IdentifierValue
              AND NameMatchKey = @NameKey AND SourceMatchKey = @SourceKey
            """, new { NameKey = nameKey, SourceKey = sourceKey, best.IdentifierType, best.IdentifierValue }, transaction);
        if (ignored is not null)
        {
            transaction.Commit();
            return new TargetMatchResult(TargetMatchOutcome.Ignored, null);
        }

        // 4c: queue it for review with the row's own data, unless it is
        // already waiting (UX_target_match_review_Pending enforces one).
        var reviewKey = await connection.QuerySingleOrDefaultAsync<int?>("""
            INSERT INTO web.target_match_review (CandidateTargetKey, IdentifierType, IdentifierValue, ImportBatchId, SourceFileName,
                                                 TargetName, TargetTypeKey, RiskScore, Description, DiscoverySource)
            OUTPUT inserted.TargetMatchReviewKey
            SELECT @CandidateTargetKey, @IdentifierType, @IdentifierValue, @ImportBatchId, @SourceFileName,
                   @TargetName, @TargetTypeKey, @RiskScore, @Description, @DiscoverySource
            WHERE NOT EXISTS (SELECT 1 FROM web.target_match_review WITH (UPDLOCK, HOLDLOCK)
                              WHERE Resolution IS NULL AND IdentifierType = @IdentifierType AND IdentifierValue = @IdentifierValue
                                AND NameMatchKey = @NameKey AND SourceMatchKey = @SourceKey);
            """, new
            {
                CandidateTargetKey = best.TargetKey, best.IdentifierType, best.IdentifierValue, NameKey = nameKey, SourceKey = sourceKey,
                ImportBatchId = importBatchId, SourceFileName = sourceFileName,
                TargetName = targetName.Trim(' '), TargetTypeKey = targetTypeKey, RiskScore = riskScore, Description = description, DiscoverySource = discoverySource
            }, transaction);

        if (reviewKey is { } newReviewKey)
        {
            foreach (var identifier in identifiers.DistinctBy(i => (i.IdentifierType, i.IdentifierValue)))
            {
                await connection.ExecuteAsync(
                    "INSERT INTO web.target_match_review_identifier (TargetMatchReviewKey, IdentifierType, IdentifierValue) VALUES (@Key, @IdentifierType, @IdentifierValue)",
                    new { Key = newReviewKey, identifier.IdentifierType, identifier.IdentifierValue }, transaction);
            }
        }

        transaction.Commit();
        return new TargetMatchResult(TargetMatchOutcome.PendingReview, null);
    }

    /// <summary>Gives <paramref name="targetKey"/> every identifier in <paramref name="identifiers"/> that no Target carries yet.</summary>
    internal static async Task AddMissingIdentifiersAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction,
        int targetKey, IEnumerable<TargetMatchIdentifier> identifiers)
    {
        foreach (var identifier in identifiers)
        {
            await connection.ExecuteAsync("""
                IF NOT EXISTS (SELECT 1 FROM web.target_identifier WHERE IdentifierType = @IdentifierType AND IdentifierValue = @IdentifierValue)
                    INSERT INTO web.target_identifier (TargetKey, IdentifierType, IdentifierValue) VALUES (@TargetKey, @IdentifierType, @IdentifierValue)
                """, new { TargetKey = targetKey, identifier.IdentifierType, identifier.IdentifierValue }, transaction);
        }
    }
}
