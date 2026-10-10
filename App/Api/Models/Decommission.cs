namespace BlueTrack.Api.Models;

/// <summary>D-186: one name pattern -- Mode is Off, Prefix, Suffix or Regex (all ignore case).</summary>
public sealed class NamePattern
{
    public required string Mode { get; init; }
    public string? Value { get; init; }
}

/// <summary>D-186: Reports > Safes Flagged for Deletion (web.vw_decom_safe).</summary>
public sealed class DecomSafeRow
{
    public int SafeKey { get; init; }
    public required string SafeName { get; init; }
    public required string SourceSystemName { get; init; }
    public string? ApplicationName { get; init; }
    public int ActiveAccountCount { get; init; }
    public int DeletedAccountCount { get; init; }
}

/// <summary>D-186: Reports > Accounts Flagged for Deletion (web.vw_decom_account).</summary>
public sealed class DecomAccountRow
{
    public long AccountKey { get; init; }
    public required string AccountName { get; init; }
    public string? UserName { get; init; }
    public string? Address { get; init; }
    public bool IsDeleted { get; init; }
    public string? SafeName { get; init; }
    public required string SourceSystemName { get; init; }
    public bool InFlaggedSafe { get; init; }
    public bool NameFlagged { get; init; }
    /// <summary>Other safes (not flagged) holding an active account with the same username + address.</summary>
    public string? OtherSafes { get; init; }
}

/// <summary>D-186: the settings page's helper -- which sample names each (unsaved) pattern matches.</summary>
public sealed class PatternTestRequest
{
    public required IReadOnlyList<string> Names { get; init; }
    public required NamePattern SafeDecom { get; init; }
    public required NamePattern AccountDecom { get; init; }
    public required NamePattern SafeIgnore { get; init; }
}

public sealed class PatternTestResult
{
    public required IReadOnlyList<PatternTestRow> Rows { get; init; }
    /// <summary>Per pattern ("SafeDecom", ...): why it can't be used, if it can't.</summary>
    public required IReadOnlyDictionary<string, string> Errors { get; init; }
}

public sealed class PatternTestRow
{
    public required string Name { get; init; }
    public bool SafeDecom { get; init; }
    public bool AccountDecom { get; init; }
    public bool SafeIgnore { get; init; }
}
