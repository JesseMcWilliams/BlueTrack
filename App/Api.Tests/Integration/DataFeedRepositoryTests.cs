using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>D-181: web.data_feed / web.data_feed_run (43_BlueTrack_DataFeeds.sql).</summary>
public class DataFeedRepositoryTests
{
    private static DataFeedRepository CreateRepository() => new(new TestDbConnectionFactory());

    private static SaveDataFeedRequest NewFeed(string name) => new()
    {
        DisplayName = name,
        FeedType = "Applications",
        FolderPath = @"C:\Feeds",
        FileNamePattern = "apps_{yyyy-MM-dd}.csv",
        IsEnabled = true,
        DisplayOrder = 5
    };

    [Fact]
    public async Task Feed_CreateUpdateDelete_RoundTrips_AndDeleteCascadesRuns()
    {
        var repository = CreateRepository();
        var userKey = await TestUsers.GetUserKeyAsync("TestUser.Admin");
        var name = $"IntegrationTestFeed_{Guid.NewGuid():N}";

        var key = await repository.CreateAsync(NewFeed(name), userKey);
        try
        {
            var created = await repository.GetAsync(key);
            Assert.NotNull(created);
            Assert.Equal("Applications", created.FeedType);
            Assert.Null(created.LastRun);
            Assert.True(await repository.DisplayNameExistsAsync(name, null));
            Assert.False(await repository.DisplayNameExistsAsync(name, key));

            Assert.True(await repository.UpdateAsync(key, new SaveDataFeedRequest
            {
                DisplayName = name, FeedType = "SafeAssignments", FolderPath = @"\\server\share", FileNamePattern = "*.csv",
                IsEnabled = false, DisplayOrder = 6
            }, userKey));
            var updated = await repository.GetAsync(key);
            Assert.Equal("SafeAssignments", updated!.FeedType);
            Assert.False(updated.IsEnabled);
            Assert.DoesNotContain(await repository.GetEnabledAsync(), f => f.DataFeedKey == key);

            var started = DateTime.UtcNow.AddMinutes(-1);
            var runKey = await repository.InsertRunAsync(new NewDataFeedRun
            {
                DataFeedKey = key, TriggerType = "RunNow", TriggeredBy = userKey, StartedAt = started, FinishedAt = DateTime.UtcNow,
                Outcome = "CompletedWithErrors", FileName = @"\\server\share\x.csv", TotalRows = 3, ErrorRows = 1,
                Summary = "2 assigned", ResultJson = "{\"errors\":[]}"
            });

            var withRun = await repository.GetAsync(key);
            Assert.Equal(runKey, withRun!.LastRun!.DataFeedRunKey);
            var run = Assert.Single(await repository.GetRunsAsync(key));
            Assert.Equal("CompletedWithErrors", run.Outcome);
            Assert.NotNull(run.TriggeredByName);
            var detail = await repository.GetRunAsync(runKey);
            Assert.Equal("{\"errors\":[]}", detail!.ResultJson);

            Assert.True(await repository.DeleteAsync(key));
            Assert.Null(await repository.GetRunAsync(runKey));
        }
        finally
        {
            await repository.DeleteAsync(key);
        }
        Assert.False(await repository.DeleteAsync(key));
    }

    [Fact]
    public async Task PurgeRunsAsync_DeletesOnlyRunsOlderThanTheRetention()
    {
        var repository = CreateRepository();
        var key = await repository.CreateAsync(NewFeed($"IntegrationTestFeed_{Guid.NewGuid():N}"), await TestUsers.GetUserKeyAsync("TestUser.Admin"));
        try
        {
            NewDataFeedRun Run(DateTime started) => new()
            {
                DataFeedKey = key, TriggerType = "Schedule", StartedAt = started, FinishedAt = started, Outcome = "Failed", ErrorMessage = "No file"
            };
            await repository.InsertRunAsync(Run(DateTime.UtcNow.AddDays(-20)));
            var recent = await repository.InsertRunAsync(Run(DateTime.UtcNow.AddDays(-2)));

            Assert.True(await repository.PurgeRunsAsync(15) >= 1);
            var remaining = Assert.Single(await repository.GetRunsAsync(key));
            Assert.Equal(recent, remaining.DataFeedRunKey);
        }
        finally
        {
            await repository.DeleteAsync(key);
        }
    }

    [Fact]
    public async Task GetSystemUserKeyAsync_FindsTheScript43SystemUser()
    {
        Assert.NotNull(await CreateRepository().GetSystemUserKeyAsync());
    }
}
