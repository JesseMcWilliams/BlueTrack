using Dapper;
using BlueTrack.Api.Models;
using BlueTrack.Api.Secrets;

namespace BlueTrack.Api.Data;

/// <summary>
/// Backs the Notifications admin page and NotificationCheckBackgroundService
/// (Design_Notifications.md, D-115). PasswordSecretReference is protected
/// via ILocalSecretProtector -- the same app-config-secret mechanism
/// IdentityProviderRepository already uses for OIDC's ClientSecret, not
/// the pluggable Secrets Store (that's for privileged-account retrieval,
/// a different concern -- see this repository's own read of the setting).
/// </summary>
public sealed class NotificationRepository(IDbConnectionFactory connectionFactory, ILocalSecretProtector localSecretProtector)
{
    private const string RedactedPlaceholder = "***";

    /// <summary>Redacted -- for the admin API/UI. Never returns a usable PasswordSecretReference value.</summary>
    public async Task<NotificationConfig> GetConfigAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT NotificationConfigKey, SmtpHost, SmtpPort, EnableStartTls, AuthMethod, Username, PasswordSecretReference, FromAddress, FromDisplayName
            FROM web.notification_config
            """;
        var config = await connection.QuerySingleAsync<NotificationConfig>(sql);
        return Redact(config);
    }

    /// <summary>
    /// Unredacted -- only for NotificationCheckBackgroundService/
    /// SmtpNotificationSender, which need the real PasswordSecretReference
    /// to Unprotect() and authenticate with. Never expose this path's
    /// result to the admin API/UI.
    /// </summary>
    public async Task<NotificationConfig> GetConfigForSendingAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT NotificationConfigKey, SmtpHost, SmtpPort, EnableStartTls, AuthMethod, Username, PasswordSecretReference, FromAddress, FromDisplayName
            FROM web.notification_config
            """;
        return await connection.QuerySingleAsync<NotificationConfig>(sql);
    }

    public async Task SaveConfigAsync(SaveNotificationConfigRequest request, int? modifiedByUserKey)
    {
        using var connection = connectionFactory.Create();

        // A blank PlaintextPassword means "leave the stored password alone" --
        // same pattern as IdentityProviderRepository.UpdateAsync.
        var setPassword = !string.IsNullOrWhiteSpace(request.PlaintextPassword);
        var sql = setPassword
            ? """
              UPDATE web.notification_config
              SET SmtpHost = @SmtpHost, SmtpPort = @SmtpPort, EnableStartTls = @EnableStartTls,
                  AuthMethod = @AuthMethod, Username = @Username, FromAddress = @FromAddress,
                  FromDisplayName = @FromDisplayName, PasswordSecretReference = @PasswordSecretReference,
                  ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
              """
            : """
              UPDATE web.notification_config
              SET SmtpHost = @SmtpHost, SmtpPort = @SmtpPort, EnableStartTls = @EnableStartTls,
                  AuthMethod = @AuthMethod, Username = @Username, FromAddress = @FromAddress,
                  FromDisplayName = @FromDisplayName,
                  ModifiedBy = @ModifiedBy, ModifiedDate = SYSUTCDATETIME()
              """;

        await connection.ExecuteAsync(sql, new
        {
            request.SmtpHost,
            request.SmtpPort,
            request.EnableStartTls,
            request.AuthMethod,
            request.Username,
            request.FromAddress,
            request.FromDisplayName,
            ModifiedBy = modifiedByUserKey,
            PasswordSecretReference = setPassword ? localSecretProtector.Protect(request.PlaintextPassword!) : null
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

    private static NotificationConfig Redact(NotificationConfig config) =>
        string.IsNullOrEmpty(config.PasswordSecretReference)
            ? config
            : new NotificationConfig
            {
                NotificationConfigKey = config.NotificationConfigKey,
                SmtpHost = config.SmtpHost,
                SmtpPort = config.SmtpPort,
                EnableStartTls = config.EnableStartTls,
                AuthMethod = config.AuthMethod,
                Username = config.Username,
                FromAddress = config.FromAddress,
                FromDisplayName = config.FromDisplayName,
                PasswordSecretReference = RedactedPlaceholder
            };
}
