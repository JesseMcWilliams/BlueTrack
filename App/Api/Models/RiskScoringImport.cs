namespace BlueTrack.Api.Models;

public sealed class TargetMatchReviewSummary
{
    public int TargetMatchReviewKey { get; init; }
    public int? CandidateTargetKey { get; init; }
    public string? CandidateTargetName { get; init; }
    public required string IdentifierType { get; init; }
    public required string IdentifierValue { get; init; }
    public string? SourceFileName { get; init; }
    public DateTime CreatedDate { get; init; }
    public string? Resolution { get; init; }

    // D-229: the incoming row's own data (null on a row queued before script 23).
    public string? TargetName { get; init; }
    public string? TargetTypeDisplayName { get; init; }
    public int? RiskScore { get; init; }
    public string? Description { get; init; }
    public string? DiscoverySource { get; init; }
    public IReadOnlyList<TargetIdentifier> Identifiers { get; init; } = [];
}

public sealed class ResolveTargetMatchReviewRequest
{
    /// <summary>
    /// D-229: 'Linked' (a new target from the incoming row, linked to
    /// PrimaryTargetKey or else the candidate) | 'NewTarget' (a new, unlinked
    /// target from the incoming row) | 'Ignored' (remembered, so the same row
    /// isn't queued again). The reviewed identifier never moves.
    /// </summary>
    public required string Resolution { get; init; }
    public int? PrimaryTargetKey { get; init; }
    /// <summary>Type code for the target Link or New Target creates; default the incoming row's, else 'Other'.</summary>
    public string? NewTargetType { get; init; }
    /// <summary>Name for the target Link or New Target creates; default the incoming row's, else the identifier value.</summary>
    public string? NewTargetName { get; init; }
    public int? NewTargetRiskScore { get; init; }
}

/// <summary>D-229: what a resolution did, for the audit log.</summary>
public sealed record TargetMatchReviewResolution(
    string IdentifierType, string IdentifierValue, string Resolution,
    int? TargetKey, string? TargetName, int? PrimaryTargetKey, string? PrimaryTargetName);

public sealed class ImportMappingProfileSummary
{
    public int ImportMappingProfileKey { get; init; }
    public required string FeedType { get; init; }
    public required string ProfileName { get; init; }
    public bool IsActive { get; init; }
    public string? Description { get; init; }
    public required IReadOnlyList<ImportMappingFieldSummary> Fields { get; init; }
}

public sealed class ImportMappingFieldSummary
{
    public required string SourceColumnName { get; init; }
    public required string TargetFieldName { get; init; }
    public bool IsRequired { get; init; }
    public string? DefaultValue { get; init; }
}

public sealed class SaveImportMappingProfileRequest
{
    public required string FeedType { get; init; }
    public required string ProfileName { get; init; }
    public bool IsActive { get; init; }
    public string? Description { get; init; }
    public required IReadOnlyList<SaveImportMappingFieldRequest> Fields { get; init; }
}

public sealed class SaveImportMappingFieldRequest
{
    public required string SourceColumnName { get; init; }
    public required string TargetFieldName { get; init; }
    public bool IsRequired { get; init; }
    public string? DefaultValue { get; init; }
}

/// <summary>Result of a bulk CSV import (any of the five risk-scoring feeds) -- per-row errors, not an all-or-nothing failure.</summary>
public sealed class ImportResultResponse
{
    public int TotalRows { get; init; }
    public int SucceededCount { get; init; }
    public int CreatedCount { get; init; }
    public int MergedCount { get; init; }
    public int PendingReviewCount { get; init; }
    /// <summary>D-229: rows skipped because the same row was ignored on Target Match Review before.</summary>
    public int IgnoredCount { get; init; }
    public required IReadOnlyList<ImportRowError> Errors { get; init; }
}

public sealed class ImportRowError
{
    public int RowNumber { get; init; }
    public required string Error { get; init; }
}
