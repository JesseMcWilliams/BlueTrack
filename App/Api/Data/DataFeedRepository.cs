using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>D-181: web.data_feed and web.data_feed_run (43_BlueTrack_DataFeeds.sql), for the Data Sources page and the feed runner.</summary>
public sealed class DataFeedRepository(IDbConnectionFactory connectionFactory)
{
    // Scheduled runs are attributed to this user (D-181): Windows' NULL SID,
    // which no real account has, so nobody can sign in as it.
    public const string SystemUserExternalIdentifier = "S-1-0-0";

    private const string FeedColumns = """
        f.DataFeedKey, f.DisplayName, f.FeedType, f.FolderPath, f.FileNamePattern,
        f.ImportMappingProfileKey, p.ProfileName AS ImportMappingProfileName, f.IsEnabled, f.DisplayOrder
        """;

    private const string RunColumns = """
        r.DataFeedRunKey, r.DataFeedKey, r.TriggerType, u.DisplayName AS TriggeredByName, r.StartedAt, r.FinishedAt,
        r.Outcome, r.FileName, r.TotalRows, r.ErrorRows, r.Summary, r.ErrorMessage
        """;

    /// <summary>Every feed in run order, each with its most recent run.</summary>
    public async Task<IReadOnlyList<DataFeedSummary>> GetAllAsync()
    {
        using var connection = connectionFactory.Create();
        var feeds = (await connection.QueryAsync<DataFeedSummary>($"""
            SELECT {FeedColumns}
            FROM web.data_feed f
            LEFT JOIN web.import_mapping_profile p ON p.ImportMappingProfileKey = f.ImportMappingProfileKey
            ORDER BY f.DisplayOrder, f.DisplayName
            """)).ToList();

        var lastRuns = (await connection.QueryAsync<DataFeedRunSummary>($"""
            SELECT {RunColumns}
            FROM web.data_feed_run r
            LEFT JOIN web.app_user u ON u.UserKey = r.TriggeredBy
            WHERE r.DataFeedRunKey IN (SELECT MAX(DataFeedRunKey) FROM web.data_feed_run GROUP BY DataFeedKey)
            """)).ToDictionary(r => r.DataFeedKey);

        foreach (var feed in feeds)
        {
            feed.LastRun = lastRuns.GetValueOrDefault(feed.DataFeedKey);
        }
        return feeds;
    }

    public async Task<IReadOnlyList<DataFeedSummary>> GetEnabledAsync() =>
        (await GetAllAsync()).Where(f => f.IsEnabled).ToList();

    public async Task<DataFeedSummary?> GetAsync(int dataFeedKey) =>
        (await GetAllAsync()).FirstOrDefault(f => f.DataFeedKey == dataFeedKey);

    public async Task<bool> DisplayNameExistsAsync(string displayName, int? exceptDataFeedKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteScalarAsync<bool>(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM web.data_feed WHERE DisplayName = @displayName AND DataFeedKey <> ISNULL(@exceptDataFeedKey, -1)) THEN 1 ELSE 0 END",
            new { displayName, exceptDataFeedKey });
    }

    public async Task<int> CreateAsync(SaveDataFeedRequest request, int userKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>("""
            INSERT INTO web.data_feed (DisplayName, FeedType, FolderPath, FileNamePattern, ImportMappingProfileKey, IsEnabled, DisplayOrder, CreatedBy)
            OUTPUT inserted.DataFeedKey
            VALUES (@DisplayName, @FeedType, @FolderPath, @FileNamePattern, @ImportMappingProfileKey, @IsEnabled, @DisplayOrder, @userKey)
            """, new
        {
            request.DisplayName, request.FeedType, request.FolderPath, request.FileNamePattern,
            request.ImportMappingProfileKey, request.IsEnabled, request.DisplayOrder, userKey
        });
    }

    /// <summary>False when the feed doesn't exist.</summary>
    public async Task<bool> UpdateAsync(int dataFeedKey, SaveDataFeedRequest request, int userKey)
    {
        using var connection = connectionFactory.Create();
        var affected = await connection.ExecuteAsync("""
            UPDATE web.data_feed
            SET DisplayName = @DisplayName, FeedType = @FeedType, FolderPath = @FolderPath, FileNamePattern = @FileNamePattern,
                ImportMappingProfileKey = @ImportMappingProfileKey, IsEnabled = @IsEnabled, DisplayOrder = @DisplayOrder,
                ModifiedBy = @userKey, ModifiedDate = SYSUTCDATETIME()
            WHERE DataFeedKey = @dataFeedKey
            """, new
        {
            request.DisplayName, request.FeedType, request.FolderPath, request.FileNamePattern,
            request.ImportMappingProfileKey, request.IsEnabled, request.DisplayOrder, userKey, dataFeedKey
        });
        return affected > 0;
    }

    /// <summary>Deletes the feed and (ON DELETE CASCADE) its run history. False when it doesn't exist.</summary>
    public async Task<bool> DeleteAsync(int dataFeedKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync("DELETE FROM web.data_feed WHERE DataFeedKey = @dataFeedKey", new { dataFeedKey }) > 0;
    }

    public async Task<long> InsertRunAsync(NewDataFeedRun run)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<long>("""
            INSERT INTO web.data_feed_run (DataFeedKey, TriggerType, TriggeredBy, StartedAt, FinishedAt, Outcome,
                                           FileName, TotalRows, ErrorRows, Summary, ResultJson, ErrorMessage)
            OUTPUT inserted.DataFeedRunKey
            VALUES (@DataFeedKey, @TriggerType, @TriggeredBy, @StartedAt, @FinishedAt, @Outcome,
                    @FileName, @TotalRows, @ErrorRows, @Summary, @ResultJson, @ErrorMessage)
            """, run);
    }

    /// <summary>A feed's runs, newest first.</summary>
    public async Task<IReadOnlyList<DataFeedRunSummary>> GetRunsAsync(int dataFeedKey, int top = 100)
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<DataFeedRunSummary>($"""
            SELECT TOP (@top) {RunColumns}
            FROM web.data_feed_run r
            LEFT JOIN web.app_user u ON u.UserKey = r.TriggeredBy
            WHERE r.DataFeedKey = @dataFeedKey
            ORDER BY r.StartedAt DESC, r.DataFeedRunKey DESC
            """, new { dataFeedKey, top })).ToList();
    }

    public async Task<DataFeedRunDetail?> GetRunAsync(long dataFeedRunKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<DataFeedRunDetail>($"""
            SELECT {RunColumns}, r.ResultJson
            FROM web.data_feed_run r
            LEFT JOIN web.app_user u ON u.UserKey = r.TriggeredBy
            WHERE r.DataFeedRunKey = @dataFeedRunKey
            """, new { dataFeedRunKey });
    }

    /// <summary>Deletes runs that started more than <paramref name="retentionDays"/> days ago; returns how many.</summary>
    public async Task<int> PurgeRunsAsync(int retentionDays)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync(
            "DELETE FROM web.data_feed_run WHERE StartedAt < DATEADD(DAY, -@retentionDays, SYSUTCDATETIME())",
            new { retentionDays });
    }

    /// <summary>The 'BlueTrack Data Feeds (system)' user that scheduled runs are attributed to; null if script 43 hasn't created it.</summary>
    public async Task<int?> GetSystemUserKeyAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<int?>("""
            SELECT u.UserKey
            FROM web.app_user u
            JOIN web.identity_provider_config p ON p.ProviderKey = u.ProviderKey AND p.ProviderType = 'WindowsIntegrated'
            WHERE u.ExternalIdentifier = @SystemUserExternalIdentifier
            """, new { SystemUserExternalIdentifier });
    }
}
