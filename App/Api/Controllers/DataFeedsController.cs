using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.DataFeeds;
using BlueTrack.Api.Models;
using BlueTrack.Api.RiskScoring;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-181 (Data Sources phase 2, Planning_Data-Sources.md): the Data Sources
/// admin page -- data feeds (CSV files in a folder, imported nightly by the
/// existing import pipelines), their Test and Run now buttons, and run history.
/// </summary>
[ApiController]
[Route("api/admin/data-feeds")]
[Authorize(Policy = Permissions.ManageDataSources)]
public sealed class DataFeedsController(
    DataFeedRepository repository,
    ImportMappingProfileRepository mappingProfileRepository,
    AppConfigRepository appConfigRepository,
    ImportMappingService importMappingService,
    DataFeedRunner runner,
    TimeProvider timeProvider,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    /// <summary>The CHECK constraint's values in 43_BlueTrack_DataFeeds.sql, with the labels the page shows.</summary>
    public static readonly IReadOnlyDictionary<string, string> FeedTypes = new Dictionary<string, string>
    {
        ["TargetInventory"] = "Target inventory",
        ["AccessGroupInventory"] = "Access group inventory",
        ["AccessGroupTargetMap"] = "Access group → target map",
        ["AccountAccessGroupMembership"] = "Account → access group membership",
        ["AccountTargetMap"] = "Account → target map",
        ["Applications"] = "Applications",
        ["SafeAssignments"] = "Safe → application assignments"
    };

    // Feed types whose import takes a D-105 mapping profile (the risk-scoring ones).
    private static readonly HashSet<string> MappedFeedTypes =
        ["TargetInventory", "AccessGroupInventory", "AccessGroupTargetMap", "AccountAccessGroupMembership", "AccountTargetMap"];

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repository.GetAllAsync());

    /// <summary>The choices for the edit page: feed types, and the mapping profiles of each.</summary>
    [HttpGet("options")]
    public async Task<IActionResult> GetOptions()
    {
        var profiles = await mappingProfileRepository.GetAllAsync();
        return Ok(new
        {
            FeedTypes = FeedTypes.Select(t => new
            {
                Value = t.Key,
                Label = t.Value,
                UsesMappingProfile = MappedFeedTypes.Contains(t.Key),
                MappingProfiles = profiles.Where(p => p.FeedType == t.Key)
                    .Select(p => new { p.ImportMappingProfileKey, p.ProfileName, p.IsActive })
            })
        });
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus()
    {
        var config = await appConfigRepository.GetAsync();
        var now = timeProvider.GetLocalNow().DateTime;
        FeedSchedule.TryParseTime(config.DataFeedRunTime, out var runTime);
        return Ok(new DataFeedStatus
        {
            IsRunning = runner.IsRunning,
            RunningFeed = runner.RunningFeed,
            WithinBusinessHours = FeedSchedule.IsWithinBusinessHours(now, config.BusinessHoursStart, config.BusinessHoursEnd, config.BusinessDays),
            NextScheduledRun = FeedSchedule.NextRun(now, runTime)
        });
    }

    [HttpGet("{dataFeedKey:int}")]
    public async Task<IActionResult> Get(int dataFeedKey) =>
        await repository.GetAsync(dataFeedKey) is { } feed ? Ok(feed) : NotFound();

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveDataFeedRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        if (await ValidateAsync(request, null) is { } problem) return problem;

        var key = await repository.CreateAsync(request, user.UserKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "data_feed", key.ToString(),
            detail: $"Data feed '{request.DisplayName}' ({request.FeedType}) created: {Path.Combine(request.FolderPath, request.FileNamePattern)}");
        return CreatedAtAction(nameof(Get), new { dataFeedKey = key }, new { dataFeedKey = key });
    }

    [HttpPut("{dataFeedKey:int}")]
    public async Task<IActionResult> Update(int dataFeedKey, [FromBody] SaveDataFeedRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        var before = await repository.GetAsync(dataFeedKey);
        if (before is null) return NotFound();
        if (await ValidateAsync(request, dataFeedKey) is { } problem) return problem;

        await repository.UpdateAsync(dataFeedKey, request, user.UserKey);

        List<FieldChange> changes = [];
        void Track(string field, string? beforeValue, string? newValue)
        {
            if (beforeValue != newValue) changes.Add(new FieldChange(field, beforeValue, newValue));
        }
        Track("DisplayName", before.DisplayName, request.DisplayName);
        Track("FeedType", before.FeedType, request.FeedType);
        Track("FolderPath", before.FolderPath, request.FolderPath);
        Track("FileNamePattern", before.FileNamePattern, request.FileNamePattern);
        Track("ImportMappingProfileKey", before.ImportMappingProfileKey?.ToString(), request.ImportMappingProfileKey?.ToString());
        Track("IsEnabled", before.IsEnabled.ToString(), request.IsEnabled.ToString());
        Track("DisplayOrder", before.DisplayOrder.ToString(), request.DisplayOrder.ToString());
        if (changes.Count > 0)
        {
            await auditLogger.LogAsync("FieldEdit", user.UserKey, "data_feed", dataFeedKey.ToString(), fieldChanges: changes);
        }
        return NoContent();
    }

    [HttpDelete("{dataFeedKey:int}")]
    public async Task<IActionResult> Delete(int dataFeedKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        var feed = await repository.GetAsync(dataFeedKey);
        if (feed is null) return NotFound();

        await repository.DeleteAsync(dataFeedKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "data_feed", dataFeedKey.ToString(),
            detail: $"Data feed '{feed.DisplayName}' deleted, with its run history");
        return NoContent();
    }

    public sealed class TestDataFeedRequest
    {
        public required string FolderPath { get; init; }
        public required string FileNamePattern { get; init; }
        /// <summary>Optional: when given, the header is checked against that import's required columns.</summary>
        public string? FeedType { get; init; }
        public int? ImportMappingProfileKey { get; init; }
    }

    /// <summary>
    /// Test: what a run would pick up right now, as the app pool sees it --
    /// works on unsaved values, so the edit page can test before saving.
    /// Reads only the file's header row, and checks it has the columns the
    /// feed's import needs (under the mapping profile's names, if one applies).
    /// </summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test([FromBody] TestDataFeedRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FolderPath)) return BadRequest(new { error = "Folder path is required." });
        if (FeedFileResolver.ValidatePattern(request.FileNamePattern) is { } patternError) return BadRequest(new { error = patternError });

        if (!Directory.Exists(request.FolderPath))
        {
            return Ok(new DataFeedTestResult { FolderFound = false, Message = "Folder not found, or the app pool can't read it." });
        }

        var today = timeProvider.GetLocalNow().DateTime;
        var expanded = FeedFileResolver.ExpandPattern(request.FileNamePattern, today);
        try
        {
            var matches = FeedFileResolver.FindMatches(request.FolderPath, request.FileNamePattern, today);
            if (matches.Count == 0)
            {
                return Ok(new DataFeedTestResult { FolderFound = true, Message = $"The folder is readable, but no file matches '{expanded}' today." });
            }

            var file = matches[0];
            await using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var columns = await CsvFileReader.ReadHeaderAsync(stream);
            var missing = await FindMissingColumnsAsync(request, columns);
            var columnsNote = missing.Count == 0 ? "" : $" Missing required columns: {string.Join(", ", missing)}.";
            return Ok(new DataFeedTestResult
            {
                FolderFound = true,
                MatchedFile = file.Name,
                MatchedFileModified = file.LastWriteTime,
                MatchingFileCount = matches.Count,
                Columns = columns,
                MissingColumns = missing,
                Message = (matches.Count == 1
                    ? $"Found {file.Name}, with {columns.Count} columns."
                    : $"{matches.Count} files match '{expanded}'; the newest, {file.Name}, would run. It has {columns.Count} columns.") + columnsNote
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Ok(new DataFeedTestResult { FolderFound = true, Message = $"Could not read the folder or file: {ex.Message}" });
        }
    }

    // The same mapping the run would use: the feed's profile, or else the
    // feed type's active one (ImportMappingService); none for the imports
    // that don't take a profile.
    private async Task<IReadOnlyList<string>> FindMissingColumnsAsync(TestDataFeedRequest request, IReadOnlyList<string> columns)
    {
        if (request.FeedType is null || !FeedTypes.ContainsKey(request.FeedType)) return [];
        var mapping = MappedFeedTypes.Contains(request.FeedType)
            ? await importMappingService.GetActiveMappingAsync(request.FeedType, request.ImportMappingProfileKey)
            : new Dictionary<string, ImportMappingService.FieldMapping>();
        return FeedColumns.FindMissing(request.FeedType, columns, mapping);
    }

    public sealed class RunDataFeedsRequest
    {
        /// <summary>The feeds to run; null or empty runs every enabled feed (Run all).</summary>
        public IReadOnlyList<int>? DataFeedKeys { get; init; }
    }

    /// <summary>Run now: 202 when started (it runs in the background; poll status), 409 if a run is already going.</summary>
    [HttpPost("run")]
    public async Task<IActionResult> Run([FromBody] RunDataFeedsRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var keys = request.DataFeedKeys is { Count: > 0 } ? request.DataFeedKeys : null;
        if (!runner.TryStartRunNow(keys, user.UserKey))
        {
            return Problem(title: "A data feed run is already in progress", detail: runner.RunningFeed is { } feed ? $"Running: {feed}" : null,
                statusCode: StatusCodes.Status409Conflict);
        }
        return Accepted();
    }

    [HttpGet("{dataFeedKey:int}/runs")]
    public async Task<IActionResult> GetRuns(int dataFeedKey) => Ok(await repository.GetRunsAsync(dataFeedKey));

    [HttpGet("runs/{dataFeedRunKey:long}")]
    public async Task<IActionResult> GetRun(long dataFeedRunKey) =>
        await repository.GetRunAsync(dataFeedRunKey) is { } run ? Ok(run) : NotFound();

    private async Task<IActionResult?> ValidateAsync(SaveDataFeedRequest request, int? dataFeedKey)
    {
        var errors = new List<string>();
        string? duplicate = null;
        if (string.IsNullOrWhiteSpace(request.DisplayName)) errors.Add("Name is required.");
        else if (request.DisplayName.Length > 200) errors.Add("Name must be 200 characters or fewer.");
        else if (await repository.DisplayNameExistsAsync(request.DisplayName.Trim(), dataFeedKey)) duplicate = $"A data feed named '{request.DisplayName}' already exists.";
        if (!FeedTypes.ContainsKey(request.FeedType ?? "")) errors.Add("Feed type is not one of the supported imports.");
        if (string.IsNullOrWhiteSpace(request.FolderPath)) errors.Add("Folder path is required.");
        else if (request.FolderPath.Length > 500) errors.Add("Folder path must be 500 characters or fewer.");
        else if (!Path.IsPathFullyQualified(request.FolderPath)) errors.Add("Folder path must be a full path, e.g. D:\\Feeds or \\\\server\\share\\feeds.");
        if (FeedFileResolver.ValidatePattern(request.FileNamePattern) is { } patternError) errors.Add(patternError);
        else if (request.FileNamePattern.Length > 260) errors.Add("File name pattern must be 260 characters or fewer.");

        if (request.ImportMappingProfileKey is { } profileKey)
        {
            var profile = (await mappingProfileRepository.GetAllAsync()).FirstOrDefault(p => p.ImportMappingProfileKey == profileKey);
            if (!MappedFeedTypes.Contains(request.FeedType ?? "")) errors.Add("This feed type doesn't use a mapping profile.");
            else if (profile is null || profile.FeedType != request.FeedType) errors.Add("The mapping profile isn't one for this feed type.");
        }

        // D-194: invalid fields are 400; a valid request whose name is taken is 409.
        if (errors.Count > 0)
        {
            if (duplicate is not null) errors.Insert(0, duplicate);
            return Problem(title: "Invalid data feed", detail: string.Join(" ", errors), statusCode: StatusCodes.Status400BadRequest);
        }
        return duplicate is null ? null : Problem(title: "Data feed already exists", detail: duplicate, statusCode: StatusCodes.Status409Conflict);
    }
}
