using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// web.app_config and web.audit_config are both singletons (exactly one
/// row each, seeded in 06_BlueTrack_WebInterface_Schema.sql) -- merged into
/// one shape for the Global Application Configuration admin page.
/// </summary>
public sealed class AppConfigRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<GlobalApplicationConfig> GetAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT ac.IdleTimeoutMinutes, ac.BreadcrumbPosition, ac.ExceptionIdPattern, ac.LockTimeoutMinutes,
                   auc.RetentionDays, auc.LogReadEvents
            FROM web.app_config ac
            CROSS JOIN web.audit_config auc
            """;
        return await connection.QuerySingleAsync<GlobalApplicationConfig>(sql);
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
                ExceptionIdPattern = @ExceptionIdPattern, LockTimeoutMinutes = @LockTimeoutMinutes
            """, new
        {
            request.IdleTimeoutMinutes,
            request.BreadcrumbPosition,
            request.ExceptionIdPattern,
            request.LockTimeoutMinutes
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
