using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the Notifications admin page and NotificationCheckBackgroundService
/// (Design_Notifications.md, D-115). D-116 migrated the SMTP credential
/// itself onto web.credential (SmtpCredentialKey) -- see CredentialRepository
/// for the actual username/password resolution, and added optional
/// per-notification-type role targeting, additive to the flat recipient
/// list below (GetTargetRoleInfoAsync).
/// </summary>
public sealed class NotificationRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<NotificationConfig> GetConfigAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT nc.NotificationConfigKey, nc.SmtpHost, nc.SmtpPort, nc.EnableStartTls, nc.AuthMethod,
                   nc.SmtpCredentialKey, c.CredentialName AS SmtpCredentialName, nc.FromAddress, nc.FromDisplayName,
                   nc.IgnoreCrlErrors, nc.IgnoreSslErrors
            FROM web.notification_config nc
            LEFT JOIN web.credential c ON c.CredentialKey = nc.SmtpCredentialKey
            """;
        return await connection.QuerySingleAsync<NotificationConfig>(sql);
    }

    /// <summary>SmtpCredentialKey is enough for SmtpNotificationSender -- CredentialRepository.ResolveForUseAsync does the actual unredacted resolution.</summary>
    public Task<NotificationConfig> GetConfigForSendingAsync() => GetConfigAsync();

    public async Task SaveConfigAsync(SaveNotificationConfigRequest request, int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            UPDATE web.notification_config
            SET SmtpHost = @SmtpHost, SmtpPort = @SmtpPort, EnableStartTls = @EnableStartTls,
                AuthMethod = @AuthMethod, SmtpCredentialKey = @SmtpCredentialKey, FromAddress = @FromAddress,
                FromDisplayName = @FromDisplayName, IgnoreCrlErrors = @IgnoreCrlErrors, IgnoreSslErrors = @IgnoreSslErrors,
                ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
            """;
        await connection.ExecuteAsync(sql, new
        {
            request.SmtpHost,
            request.SmtpPort,
            request.EnableStartTls,
            request.AuthMethod,
            request.SmtpCredentialKey,
            request.FromAddress,
            request.FromDisplayName,
            request.IgnoreCrlErrors,
            request.IgnoreSslErrors,
            ModifiedBy = modifiedByUserKey
        });
    }

    public async Task<IReadOnlyList<NotificationRecipient>> GetRecipientsAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = "SELECT RecipientKey, Email, DisplayName, IsActive FROM web.notification_recipient ORDER BY Email";
        var rows = await connection.QueryAsync<NotificationRecipient>(sql);
        return rows.AsList();
    }

    /// <summary>Only the recipients an actual send should go to.</summary>
    public async Task<IReadOnlyList<string>> GetActiveRecipientEmailsAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = "SELECT Email FROM web.notification_recipient WHERE IsActive = 1 ORDER BY Email";
        var rows = await connection.QueryAsync<string>(sql);
        return rows.AsList();
    }

    public async Task<int> CreateRecipientAsync(SaveNotificationRecipientRequest request)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            INSERT INTO web.notification_recipient (Email, DisplayName, IsActive)
            OUTPUT inserted.RecipientKey
            VALUES (@Email, @DisplayName, @IsActive)
            """;
        return await connection.QuerySingleAsync<int>(sql, request);
    }

    public async Task UpdateRecipientAsync(int recipientKey, SaveNotificationRecipientRequest request)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            UPDATE web.notification_recipient
            SET Email = @Email, DisplayName = @DisplayName, IsActive = @IsActive
            WHERE RecipientKey = @RecipientKey
            """;
        await connection.ExecuteAsync(sql, new { RecipientKey = recipientKey, request.Email, request.DisplayName, request.IsActive });
    }

    public async Task DeleteRecipientAsync(int recipientKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = "DELETE FROM web.notification_recipient WHERE RecipientKey = @RecipientKey";
        await connection.ExecuteAsync(sql, new { RecipientKey = recipientKey });
    }

    public async Task<IReadOnlyList<NotificationTypeSummary>> GetNotificationTypesAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT nt.NotificationTypeKey, nt.NotificationTypeName, nt.Description, nt.TargetRoleKey, r.RoleName AS TargetRoleName, nt.IsEnabled
            FROM web.dim_notification_type nt
            LEFT JOIN web.app_role r ON r.AppRoleKey = nt.TargetRoleKey
            ORDER BY nt.NotificationTypeName
            """;
        var rows = await connection.QueryAsync<NotificationTypeSummary>(sql);
        return rows.AsList();
    }

    public async Task SetNotificationTypeTargetRoleAsync(int notificationTypeKey, int? targetRoleKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = "UPDATE web.dim_notification_type SET TargetRoleKey = @TargetRoleKey WHERE NotificationTypeKey = @NotificationTypeKey";
        await connection.ExecuteAsync(sql, new { NotificationTypeKey = notificationTypeKey, TargetRoleKey = targetRoleKey });
    }

    /// <summary>
    /// D-116's additive role-targeting: the target role's own NotificationEmail
    /// (if set) plus the AD group identifiers mapped to that role (for
    /// LdapGroupMemberResolver to expand) -- both empty/null when the
    /// notification type has no TargetRoleKey, which is the default and
    /// preserves D-115's exact flat-list-only behavior.
    /// </summary>
    public async Task<(string? RoleEmail, IReadOnlyList<string> MappedGroupIdentifiers)> GetTargetRoleInfoAsync(string notificationTypeName)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT r.NotificationEmail
            FROM web.dim_notification_type nt
            JOIN web.app_role r ON r.AppRoleKey = nt.TargetRoleKey
            WHERE nt.NotificationTypeName = @NotificationTypeName
            """;
        var roleEmail = await connection.QuerySingleOrDefaultAsync<string?>(sql, new { NotificationTypeName = notificationTypeName });

        const string groupsSql = """
            SELECT gm.IdentityGroupName
            FROM web.dim_notification_type nt
            JOIN web.identity_group_role_map gm ON gm.AppRoleKey = nt.TargetRoleKey
            WHERE nt.NotificationTypeName = @NotificationTypeName
            """;
        var groups = await connection.QueryAsync<string>(groupsSql, new { NotificationTypeName = notificationTypeName });

        return (roleEmail, groups.AsList());
    }

    /// <summary>
    /// De-duplication check (Design_Notifications.md): has this alert type
    /// already been sent within the given cooldown, so a scheduled check
    /// doesn't re-send every time it runs while the underlying condition
    /// stays true.
    /// </summary>
    public async Task<bool> WasSentRecentlyAsync(string notificationTypeName, TimeSpan cooldown)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT COUNT(*)
            FROM web.notification_log nl
            JOIN web.dim_notification_type nt ON nt.NotificationTypeKey = nl.NotificationTypeKey
            WHERE nt.NotificationTypeName = @NotificationTypeName
              AND nl.SentDate >= DATEADD(SECOND, -@CooldownSeconds, SYSUTCDATETIME())
            """;
        // The cutoff is computed by SQL Server, on the same clock and at the
        // same precision as SentDate (SYSUTCDATETIME), rather than from the
        // app's DateTime.UtcNow, which Dapper sends as `datetime` (rounded to
        // ~3 ms) and which can differ from SQL Server's clock on another host.
        var count = await connection.QuerySingleAsync<int>(sql, new
        {
            NotificationTypeName = notificationTypeName,
            CooldownSeconds = (int)Math.Min(int.MaxValue, Math.Ceiling(cooldown.TotalSeconds))
        });
        return count > 0;
    }

    public async Task RecordSentAsync(string notificationTypeName, string? detail)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            INSERT INTO web.notification_log (NotificationTypeKey, Detail)
            SELECT NotificationTypeKey, @Detail
            FROM web.dim_notification_type
            WHERE NotificationTypeName = @NotificationTypeName
            """;
        await connection.ExecuteAsync(sql, new { NotificationTypeName = notificationTypeName, Detail = detail });
    }

    /// <summary>D-222: a type that's off (or doesn't exist) is never evaluated or sent.</summary>
    public async Task<bool> IsEnabledAsync(string notificationTypeName)
    {
        using var connection = connectionFactory.Create();
        const string sql = "SELECT IsEnabled FROM web.dim_notification_type WHERE NotificationTypeName = @NotificationTypeName";
        return await connection.QuerySingleOrDefaultAsync<bool?>(sql, new { NotificationTypeName = notificationTypeName }) ?? false;
    }

    public async Task SetNotificationTypeEnabledAsync(int notificationTypeKey, bool isEnabled)
    {
        using var connection = connectionFactory.Create();
        const string sql = "UPDATE web.dim_notification_type SET IsEnabled = @IsEnabled WHERE NotificationTypeKey = @NotificationTypeKey";
        await connection.ExecuteAsync(sql, new { NotificationTypeKey = notificationTypeKey, IsEnabled = isEnabled });
    }

    /// <summary>D-222: whether a send for this run/day/week (DedupKey) is already logged.</summary>
    public async Task<bool> WasSentAsync(string notificationTypeName, string dedupKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT COUNT(*)
            FROM web.notification_log nl
            JOIN web.dim_notification_type nt ON nt.NotificationTypeKey = nl.NotificationTypeKey
            WHERE nt.NotificationTypeName = @NotificationTypeName AND nl.DedupKey = @DedupKey
            """;
        return await connection.QuerySingleAsync<int>(sql, new { NotificationTypeName = notificationTypeName, DedupKey = dedupKey }) > 0;
    }

    /// <summary>
    /// D-222: records the send before it happens, under a key that's unique
    /// per type (UX_notification_log_Type_DedupKey), so two cycles or two API
    /// processes can't both send it. Null when it's already claimed. A failed
    /// send calls ReleaseClaimAsync, so the row still means "sent".
    /// </summary>
    public async Task<long?> TryClaimAsync(string notificationTypeName, string dedupKey, string? detail)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            INSERT INTO web.notification_log (NotificationTypeKey, Detail, DedupKey)
            OUTPUT inserted.NotificationLogKey
            SELECT NotificationTypeKey, @Detail, @DedupKey
            FROM web.dim_notification_type
            WHERE NotificationTypeName = @NotificationTypeName
            """;
        try
        {
            return await connection.QuerySingleOrDefaultAsync<long?>(sql, new { NotificationTypeName = notificationTypeName, DedupKey = dedupKey, Detail = Truncate(detail, 2000) });
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
        {
            return null;   // Duplicate key: already sent (or being sent).
        }
    }

    public async Task ReleaseClaimAsync(long notificationLogKey)
    {
        using var connection = connectionFactory.Create();
        await connection.ExecuteAsync("DELETE FROM web.notification_log WHERE NotificationLogKey = @NotificationLogKey", new { NotificationLogKey = notificationLogKey });
    }

    public async Task<NotificationSchedule> GetScheduleAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT CONVERT(varchar(5), DailyDigestTime, 108) AS DailyDigestTime, WeeklySummaryDay,
                   CONVERT(varchar(5), WeeklySummaryTime, 108) AS WeeklySummaryTime
            FROM web.notification_config
            """;
        return await connection.QuerySingleAsync<NotificationSchedule>(sql);
    }

    public async Task SaveScheduleAsync(NotificationSchedule schedule, int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            UPDATE web.notification_config
            SET DailyDigestTime = CAST(@DailyDigestTime AS time(0)), WeeklySummaryDay = @WeeklySummaryDay,
                WeeklySummaryTime = CAST(@WeeklySummaryTime AS time(0)),
                ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
            """;
        await connection.ExecuteAsync(sql, new { schedule.DailyDigestTime, schedule.WeeklySummaryDay, schedule.WeeklySummaryTime, ModifiedBy = modifiedByUserKey });
    }

    private static string? Truncate(string? value, int length) => value is null || value.Length <= length ? value : value[..length];
}
