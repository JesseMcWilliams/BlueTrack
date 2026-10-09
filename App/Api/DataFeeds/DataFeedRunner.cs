using System.Text.Json;
using BlueTrack.Api.Data;
using BlueTrack.Api.Imports;
using BlueTrack.Api.Models;
using BlueTrack.Api.RiskScoring;

namespace BlueTrack.Api.DataFeeds;

/// <summary>
/// D-181: runs data feeds -- for each feed, finds its newest matching file,
/// reads it as CSV and hands the rows to the same import service the Bulk
/// Actions upload uses (phase 1, App/Api/Imports/), then records the run in
/// web.data_feed_run. Feeds run one at a time, in DisplayOrder, and one
/// feed's failure doesn't stop the next.
///
/// Singleton, so the "one run at a time" gate is shared by the nightly
/// schedule and every Run now click: a Run now while a run is going is
/// refused (409 at the API), while the nightly run waits its turn. Each run
/// gets its own DI scope, as the background services here do.
/// </summary>
public sealed class DataFeedRunner(
    IServiceScopeFactory scopeFactory,
    ILogger<DataFeedRunner> logger,
    TimeProvider timeProvider)
{
    public const string TriggerSchedule = "Schedule";
    public const string TriggerRunNow = "RunNow";

    private static readonly JsonSerializerOptions ResultJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly SemaphoreSlim gate = new(1, 1);
    private volatile string? runningFeed;

    public bool IsRunning => gate.CurrentCount == 0;
    public string? RunningFeed => runningFeed;

    /// <summary>
    /// Run now: starts the given feeds (all enabled feeds when null) in the
    /// background and returns at once. False if a run is already going.
    /// </summary>
    public bool TryStartRunNow(IReadOnlyCollection<int>? dataFeedKeys, int userKey)
    {
        if (!gate.Wait(0)) return false;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunFeedsAsync(dataFeedKeys, TriggerRunNow, userKey, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Data feed Run now failed unexpectedly.");
            }
            finally
            {
                runningFeed = null;
                gate.Release();
            }
        });
        return true;
    }

    /// <summary>The nightly run: every enabled feed, attributed to the system user; waits for a Run now in progress to finish.</summary>
    public async Task RunScheduledAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            await RunFeedsAsync(null, TriggerSchedule, userKey: null, cancellationToken);
        }
        finally
        {
            runningFeed = null;
            gate.Release();
        }
    }

    private async Task RunFeedsAsync(IReadOnlyCollection<int>? dataFeedKeys, string triggerType, int? userKey, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<DataFeedRepository>();

        // Scheduled runs: the system user. Run now: the user who clicked.
        var attributedUserKey = userKey ?? await repository.GetSystemUserKeyAsync();

        var feeds = dataFeedKeys is null
            ? await repository.GetEnabledAsync()
            : (await repository.GetAllAsync()).Where(f => dataFeedKeys.Contains(f.DataFeedKey)).ToList();

        foreach (var feed in feeds)
        {
            if (cancellationToken.IsCancellationRequested) break;
            runningFeed = feed.DisplayName;

            // A fresh scope per feed, so one feed's state (e.g. a broken
            // connection) can't leak into the next.
            using var feedScope = scopeFactory.CreateScope();
            var run = await RunOneAsync(feedScope.ServiceProvider, feed, triggerType, attributedUserKey);
            try
            {
                await repository.InsertRunAsync(run);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not record the run of data feed {Feed}.", feed.DisplayName);
            }
        }
    }

    private async Task<NewDataFeedRun> RunOneAsync(IServiceProvider services, DataFeedSummary feed, string triggerType, int? userKey)
    {
        var startedAt = timeProvider.GetUtcNow().UtcDateTime;
        NewDataFeedRun Failed(string message, string? fileName = null) => new()
        {
            DataFeedKey = feed.DataFeedKey, TriggerType = triggerType, TriggeredBy = userKey,
            StartedAt = startedAt, FinishedAt = timeProvider.GetUtcNow().UtcDateTime,
            Outcome = "Failed", FileName = fileName, ErrorMessage = Truncate(message, 2000)
        };

        FileInfo file;
        try
        {
            if (!Directory.Exists(feed.FolderPath))
                return Failed($"Folder not found, or the app pool can't read it: {feed.FolderPath}");
            var matches = FeedFileResolver.FindMatches(feed.FolderPath, feed.FileNamePattern, timeProvider.GetLocalNow().DateTime);
            if (matches.Count == 0)
                return Failed($"No file matching '{FeedFileResolver.ExpandPattern(feed.FileNamePattern, timeProvider.GetLocalNow().DateTime)}' in {feed.FolderPath}");
            file = matches[0];
        }
        catch (Exception ex)
        {
            return Failed($"Could not search the folder: {ex.Message}");
        }

        try
        {
            IReadOnlyList<IReadOnlyDictionary<string, string>> rows;
            // FileShare.ReadWrite: the file is left in place, and whatever
            // drops it there may still have it open.
            await using (var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                rows = await CsvFileReader.ReadRowsAsync(stream);
            }

            var (totalRows, errorRows, summary, result) = await ImportAsync(services, feed, rows, file.Name, userKey);
            return new NewDataFeedRun
            {
                DataFeedKey = feed.DataFeedKey, TriggerType = triggerType, TriggeredBy = userKey,
                StartedAt = startedAt, FinishedAt = timeProvider.GetUtcNow().UtcDateTime,
                Outcome = errorRows == 0 ? "Succeeded" : "CompletedWithErrors",
                FileName = Truncate(file.FullName, 500), TotalRows = totalRows, ErrorRows = errorRows,
                Summary = Truncate(summary, 500), ResultJson = JsonSerializer.Serialize(result, ResultJsonOptions)
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Data feed {Feed} failed on {File}.", feed.DisplayName, file.FullName);
            return Failed(ex.Message, Truncate(file.FullName, 500));
        }
    }

    /// <summary>Hands the rows to the import that matches the feed type; returns counts, a one-line summary and the import's own result.</summary>
    private static async Task<(int Total, int Errors, string Summary, object Result)> ImportAsync(
        IServiceProvider services, DataFeedSummary feed, IReadOnlyList<IReadOnlyDictionary<string, string>> rows, string fileName, int? userKey)
    {
        var riskScoring = services.GetRequiredService<RiskScoringImportService>();
        var applications = services.GetRequiredService<ApplicationMappingImportService>();
        var profile = feed.ImportMappingProfileKey;

        if (feed.FeedType is "Applications" or "SafeAssignments" or "TargetInventory" && userKey is null)
            throw new InvalidOperationException("No user to attribute the import to: the 'BlueTrack Data Feeds (system)' user is missing (run Database script 43).");

        switch (feed.FeedType)
        {
            case "Applications":
            {
                var r = await applications.ImportApplicationsAsync(rows, userKey!.Value);
                return (r.TotalRows, r.Errors.Count, $"{r.CreatedCount} created, {r.UpdatedCount} updated, {r.UnchangedCount} unchanged, {r.Errors.Count} errors", r);
            }
            case "SafeAssignments":
            {
                var r = await applications.ImportSafeAssignmentsAsync(rows, userKey!.Value);
                return (r.TotalRows, r.Errors.Count, $"{r.AssignedCount} assigned, {r.ChangedCount} changed, {r.UnchangedCount} unchanged, {r.Errors.Count} errors", r);
            }
        }

        var result = feed.FeedType switch
        {
            "TargetInventory" => await riskScoring.ImportTargetInventoryAsync(rows, profile, fileName, userKey!.Value),
            "AccessGroupInventory" => await riskScoring.ImportAccessGroupInventoryAsync(rows, profile, fileName),
            "AccessGroupTargetMap" => await riskScoring.ImportAccessGroupTargetMapAsync(rows, profile, fileName),
            "AccountAccessGroupMembership" => await riskScoring.ImportAccountAccessGroupMembershipAsync(rows, profile, fileName),
            "AccountTargetMap" => await riskScoring.ImportAccountTargetMapAsync(rows, profile, fileName),
            _ => throw new InvalidOperationException($"Unknown feed type '{feed.FeedType}'.")
        };
        var summary = $"{result.SucceededCount} succeeded ({result.CreatedCount} created, {result.MergedCount} merged, {result.PendingReviewCount} pending review), {result.Errors.Count} errors";
        return (result.TotalRows, result.Errors.Count, summary, result);
    }

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..(max - 1)] + "…";
}
