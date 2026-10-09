namespace BlueTrack.Api.Models;

/// <summary>
/// D-181 (Data Sources phase 2): one web.data_feed row -- a CSV file in a
/// folder that is imported nightly (or on Run now) by one of the existing
/// import pipelines (App/Api/Imports/), picked by FeedType.
/// </summary>
public sealed class DataFeedSummary
{
    public int DataFeedKey { get; init; }
    public required string DisplayName { get; init; }
    public required string FeedType { get; init; }
    public required string FolderPath { get; init; }
    public required string FileNamePattern { get; init; }
    public int? ImportMappingProfileKey { get; init; }
    public string? ImportMappingProfileName { get; init; }
    public bool IsEnabled { get; init; }
    public int DisplayOrder { get; init; }
    public DataFeedRunSummary? LastRun { get; set; }
}

public sealed class SaveDataFeedRequest
{
    public required string DisplayName { get; init; }
    public required string FeedType { get; init; }
    public required string FolderPath { get; init; }
    public required string FileNamePattern { get; init; }
    public int? ImportMappingProfileKey { get; init; }
    public bool IsEnabled { get; init; } = true;
    public int DisplayOrder { get; init; }
}

/// <summary>One web.data_feed_run row, without the (possibly large) ResultJson.</summary>
public class DataFeedRunSummary
{
    public long DataFeedRunKey { get; init; }
    public int DataFeedKey { get; init; }
    public required string TriggerType { get; init; }
    public string? TriggeredByName { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? FinishedAt { get; init; }
    public required string Outcome { get; init; }
    public string? FileName { get; init; }
    public int? TotalRows { get; init; }
    public int? ErrorRows { get; init; }
    public string? Summary { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>A run with the import's own result (row errors included), as JSON.</summary>
public sealed class DataFeedRunDetail : DataFeedRunSummary
{
    public string? ResultJson { get; init; }
}

/// <summary>What the runner records for one feed when it finishes.</summary>
public sealed class NewDataFeedRun
{
    public int DataFeedKey { get; init; }
    public required string TriggerType { get; init; }
    public int? TriggeredBy { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime FinishedAt { get; init; }
    public required string Outcome { get; init; }
    public string? FileName { get; init; }
    public int? TotalRows { get; init; }
    public int? ErrorRows { get; init; }
    public string? Summary { get; init; }
    public string? ResultJson { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>The Test button's answer: can the app pool see the folder, which file would run, and its header.</summary>
public sealed class DataFeedTestResult
{
    public bool FolderFound { get; init; }
    public string? MatchedFile { get; init; }
    public DateTime? MatchedFileModified { get; init; }
    public int MatchingFileCount { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = [];
    /// <summary>Required columns the file lacks for the feed's import (and mapping profile); empty when it has them all, or when no import was given.</summary>
    public IReadOnlyList<string> MissingColumns { get; init; } = [];
    public required string Message { get; init; }
}

/// <summary>The Data Sources page header: is a run going, is it business hours, when is the next nightly run.</summary>
public sealed class DataFeedStatus
{
    public bool IsRunning { get; init; }
    public string? RunningFeed { get; init; }
    public bool WithinBusinessHours { get; init; }
    public DateTime NextScheduledRun { get; init; }
}
