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
                   nc.SmtpCredentialKey, c.CredentialName AS SmtpCredentialName, nc.FromAddress, nc.FromDisplayName
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
                FromDisplayName = @FromDisplayName, ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
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
            SELECT nt.NotificationTypeKey, nt.NotificationTypeName, nt.Description, nt.TargetRoleKey, r.RoleName AS TargetRoleName
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
              AND nl.SentDate >= @Cutoff
            """;
        var count = await connection.QuerySingleAsync<int>(sql, new
        {
            NotificationTypeName = notificationTypeName,
            Cutoff = DateTime.UtcNow - cooldown
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
}
