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
}

public sealed class ResolveTargetMatchReviewRequest
{
    /// <summary>'Merged' (into CandidateTargetKey, or MergeIntoTargetKey if supplied) | 'NewTarget' | 'Ignored'.</summary>
    public required string Resolution { get; init; }
    public int? MergeIntoTargetKey { get; init; }
    public string? NewTargetType { get; init; }
    public string? NewTargetName { get; init; }
    public int? NewTargetRiskScore { get; init; }
}

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
    public required IReadOnlyList<ImportRowError> Errors { get; init; }
}

public sealed class ImportRowError
{
    public int RowNumber { get; init; }
    public required string Error { get; init; }
}
