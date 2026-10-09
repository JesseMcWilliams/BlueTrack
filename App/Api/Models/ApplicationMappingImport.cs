namespace BlueTrack.Api.Models;

/// <summary>D-180: result of the Applications CSV import -- per-row errors, not an all-or-nothing failure.</summary>
public sealed class ApplicationImportResult
{
    public int TotalRows { get; init; }
    public int CreatedCount { get; init; }
    public int UpdatedCount { get; init; }
    public int UnchangedCount { get; init; }
    public required IReadOnlyList<ImportRowError> Errors { get; init; }
}

/// <summary>
/// D-180: result of the Safe → Application assignments CSV import. Counts
/// are per safe, not per row: a row's safe name can match a safe in more
/// than one source, and each matching safe is assigned.
/// </summary>
public sealed class SafeAssignmentImportResult
{
    public int TotalRows { get; init; }
    public int AssignedCount { get; init; }   // had no application before
    public int ChangedCount { get; init; }    // had a different application (overwritten)
    public int UnchangedCount { get; init; }  // already had this application
    public required IReadOnlyList<ImportRowError> Errors { get; init; }
}

/// <summary>One dbo.dim_safe row with its source, for matching import rows by name.</summary>
public sealed class SafeForMatching
{
    public int SafeKey { get; init; }
    public required string SafeName { get; init; }
    public int SourceSystemKey { get; init; }
    public required string SourceSystemName { get; init; }
    public int? ApplicationKey { get; set; }
}
