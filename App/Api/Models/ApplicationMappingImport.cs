namespace BlueTrack.Api.Models;

/// <summary>D-180: result of the Applications CSV import -- per-row errors, not an all-or-nothing failure.</summary>
public sealed class ApplicationImportResult
{
    /// <summary>UI review W8-3: true when this was a preview (dryRun=true): the counts are what the file would do, and nothing was changed.</summary>
    public bool DryRun { get; init; }
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
    public bool DryRun { get; init; }
    public int TotalRows { get; init; }
    public int AssignedCount { get; init; }   // had no application before
    public int ChangedCount { get; init; }    // had a different application (overwritten)
    public int UnchangedCount { get; init; }  // already had this application
    public required IReadOnlyList<ImportRowError> Errors { get; init; }
}

/// <summary>
/// D-239: result of the Account → Application assignments CSV import. Counts
/// are per account, not per row: a username and address can match accounts in
/// more than one source, and each matching account is assigned.
/// </summary>
public sealed class AccountAssignmentImportResult
{
    public bool DryRun { get; init; }
    public int TotalRows { get; init; }
    public int AssignedCount { get; init; }   // had no application of its own before
    public int ChangedCount { get; init; }    // had a different application (overwritten)
    public int UnchangedCount { get; init; }  // already had this application
    public required IReadOnlyList<ImportRowError> Errors { get; init; }
}

/// <summary>D-239: an active account matched by username and address, with its own application.</summary>
public sealed class AccountForMatching
{
    public long AccountKey { get; init; }
    public required string AccountName { get; init; }
    public required string SourceSystemName { get; init; }
    public int? ApplicationKey { get; init; }
    public bool HasProgress { get; init; }
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
