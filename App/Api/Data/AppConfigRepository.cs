using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// web.app_config and web.audit_config are both singletons (exactly one
/// row each, seeded in 04_BlueTrack_Baseline_WebSchema.sql) -- merged into
/// one shape for the Global Application Configuration admin page.
/// </summary>
public sealed class AppConfigRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<GlobalApplicationConfig> GetAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT ac.IdleTimeoutMinutes, ac.BreadcrumbPosition, ac.ExceptionIdPattern, ac.LockTimeoutMinutes,
                   ac.BackupFolder, ac.ActiveRiskAlgorithm, ac.EnforceRiskExceptionSegregationOfDuties,
                   auc.RetentionDays, auc.LogReadEvents,
                   CONVERT(varchar(5), ac.DataFeedRunTime, 108) AS DataFeedRunTime, ac.DataFeedRunRetentionDays,
                   CONVERT(varchar(5), ac.ScheduledBackupTime, 108) AS ScheduledBackupTime, ac.ScheduledBackupRetentionDays,
                   CONVERT(varchar(5), ac.BusinessHoursStart, 108) AS BusinessHoursStart,
                   CONVERT(varchar(5), ac.BusinessHoursEnd, 108) AS BusinessHoursEnd, ac.BusinessDays,
                   ac.BulkEditMaxAccounts, ac.ExportMaxRows, ac.ApiKeyDefaultLifetimeDays, ac.ApiRateLimitPerMinute, ac.ApiKeyReminderLeadDays,
                   ac.SafeDecomMode, ac.SafeDecomValue, ac.AccountDecomMode, ac.AccountDecomValue,
                   ac.SafeIgnoreMode, ac.SafeIgnoreValue, dbo.fn_RegexSupported() AS RegexSupported,
                   ac.SiteUrl
            FROM web.app_config ac
            CROSS JOIN web.audit_config auc
            """;
        return await connection.QuerySingleAsync<GlobalApplicationConfig>(sql);
    }

    /// <summary>D-224: the address the emails link to pages under; null when not set.</summary>
    public async Task<string?> GetSiteUrlAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<string?>("SELECT SiteUrl FROM web.app_config");
    }

    /// <summary>D-243: the most requests one API key may make in a minute.</summary>
    public async Task<int> GetApiRateLimitPerMinuteAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<int>("SELECT ApiRateLimitPerMinute FROM web.app_config");
    }

    /// <summary>D-35/D-83: checked on every detail-view GET before logging a RecordViewed event.</summary>
    public async Task<bool> IsLogReadEventsEnabledAsync()
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleAsync<bool>("SELECT LogReadEvents FROM web.audit_config");
    }

    public async Task UpdateAsync(SaveGlobalApplicationConfigRequest request, int modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();

        await connection.ExecuteAsync("""
            UPDATE web.app_config
            SET IdleTimeoutMinutes = @IdleTimeoutMinutes, BreadcrumbPosition = @BreadcrumbPosition,
                ExceptionIdPattern = @ExceptionIdPattern, LockTimeoutMinutes = @LockTimeoutMinutes,
                BackupFolder = @BackupFolder, ActiveRiskAlgorithm = @ActiveRiskAlgorithm,
                EnforceRiskExceptionSegregationOfDuties = @EnforceRiskExceptionSegregationOfDuties,
                -- D-181: null = leave unchanged (older callers don't send these).
                DataFeedRunTime = COALESCE(CAST(@DataFeedRunTime AS time(0)), DataFeedRunTime),
                DataFeedRunRetentionDays = COALESCE(@DataFeedRunRetentionDays, DataFeedRunRetentionDays),
                -- D-200: null = unchanged, '' = off.
                ScheduledBackupTime = CASE WHEN @ScheduledBackupTime IS NULL THEN ScheduledBackupTime
                                           WHEN @ScheduledBackupTime = '' THEN NULL
                                           ELSE CAST(@ScheduledBackupTime AS time(0)) END,
                ScheduledBackupRetentionDays = COALESCE(@ScheduledBackupRetentionDays, ScheduledBackupRetentionDays),
                BusinessHoursStart = COALESCE(CAST(@BusinessHoursStart AS time(0)), BusinessHoursStart),
                BusinessHoursEnd = COALESCE(CAST(@BusinessHoursEnd AS time(0)), BusinessHoursEnd),
                BusinessDays = COALESCE(@BusinessDays, BusinessDays),
                BulkEditMaxAccounts = COALESCE(@BulkEditMaxAccounts, BulkEditMaxAccounts),
                ExportMaxRows = COALESCE(@ExportMaxRows, ExportMaxRows),   -- D-220
                ApiKeyDefaultLifetimeDays = COALESCE(@ApiKeyDefaultLifetimeDays, ApiKeyDefaultLifetimeDays),   -- D-243
                ApiRateLimitPerMinute = COALESCE(@ApiRateLimitPerMinute, ApiRateLimitPerMinute),   -- D-243
                ApiKeyReminderLeadDays = COALESCE(@ApiKeyReminderLeadDays, ApiKeyReminderLeadDays),   -- D-243
                -- D-186: a pattern's value changes only when its mode is sent; Off clears it.
                SafeDecomValue = CASE WHEN @SafeDecomMode IS NULL THEN SafeDecomValue WHEN @SafeDecomMode = 'Off' THEN NULL ELSE @SafeDecomValue END,
                SafeDecomMode = COALESCE(@SafeDecomMode, SafeDecomMode),
                AccountDecomValue = CASE WHEN @AccountDecomMode IS NULL THEN AccountDecomValue WHEN @AccountDecomMode = 'Off' THEN NULL ELSE @AccountDecomValue END,
                AccountDecomMode = COALESCE(@AccountDecomMode, AccountDecomMode),
                SafeIgnoreValue = CASE WHEN @SafeIgnoreMode IS NULL THEN SafeIgnoreValue WHEN @SafeIgnoreMode = 'Off' THEN NULL ELSE @SafeIgnoreValue END,
                SafeIgnoreMode = COALESCE(@SafeIgnoreMode, SafeIgnoreMode),
                -- D-224: null = unchanged, '' = not set.
                SiteUrl = CASE WHEN @SiteUrl IS NULL THEN SiteUrl WHEN @SiteUrl = '' THEN NULL ELSE @SiteUrl END
            """, new
        {
            request.IdleTimeoutMinutes,
            request.BreadcrumbPosition,
            request.ExceptionIdPattern,
            request.LockTimeoutMinutes,
            request.BackupFolder,
            request.ActiveRiskAlgorithm,
            request.EnforceRiskExceptionSegregationOfDuties,
            request.DataFeedRunTime,
            request.DataFeedRunRetentionDays,
            request.ScheduledBackupTime,
            request.ScheduledBackupRetentionDays,
            request.BusinessHoursStart,
            request.BusinessHoursEnd,
            request.BusinessDays,
            request.BulkEditMaxAccounts,
            request.ExportMaxRows,
            request.ApiKeyDefaultLifetimeDays,
            request.ApiRateLimitPerMinute,
            request.ApiKeyReminderLeadDays,
            request.SafeDecomMode,
            request.SafeDecomValue,
            request.AccountDecomMode,
            request.AccountDecomValue,
            request.SafeIgnoreMode,
            request.SafeIgnoreValue,
            request.SiteUrl
        }, transaction);

        await connection.ExecuteAsync("""
            UPDATE web.audit_config
            SET RetentionDays = @RetentionDays, LogReadEvents = @LogReadEvents,
                ModifiedBy = @ModifiedByUserKey, ModifiedDate = SYSUTCDATETIME()
            """, new
        {
            request.RetentionDays,
            request.LogReadEvents,
            ModifiedByUserKey = modifiedByUserKey
        }, transaction);

        transaction.Commit();
    }
}
