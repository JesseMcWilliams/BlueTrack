using BlueTrack.Api.Data;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// D-216: emails the BreakGlassLogon notification type's recipients when a
/// designated break-glass account signs in. Sent in the background with its
/// own scope, so a slow or unreachable mail server never holds up the
/// sign-in; a failure is logged (the BreakGlassLogon audit event is written
/// in the request either way). No cooldown: every sign-in alerts.
/// </summary>
public sealed class BreakGlassAlerter(IServiceScopeFactory scopeFactory, ILogger<BreakGlassAlerter> logger)
{
    public const string NotificationTypeName = "BreakGlassLogon";

    public Task SendInBackground(string accountName, string providerType, string? sourceIpAddress, DateTime signedInUtc) =>
        Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                // D-222: the type can be turned off on the Notifications page (the audit event is still written).
                if (!await scope.ServiceProvider.GetRequiredService<NotificationRepository>().IsEnabledAsync(NotificationTypeName))
                {
                    return;
                }
                var recipients = await scope.ServiceProvider.GetRequiredService<NotificationRecipientResolver>().ResolveAsync(NotificationTypeName);
                if (recipients.Count == 0)
                {
                    logger.LogWarning("Break-glass account {Account} signed in, but no notification recipients are configured -- no email sent.", accountName);
                    return;
                }

                var subject = $"BlueTrack: break-glass account {accountName} signed in";
                // D-224: a link to the Audit Log when the Site URL is set (plain text, so no encoding beyond the URL's own).
                var auditLog = SiteLinks.Describe(await scope.ServiceProvider.GetRequiredService<AppConfigRepository>().GetSiteUrlAsync(), SiteLinks.AuditLog);
                var body = BuildBody(accountName, providerType, sourceIpAddress, signedInUtc, auditLog);
                await scope.ServiceProvider.GetRequiredService<INotificationSender>().SendAsync(recipients, subject, body);
                await scope.ServiceProvider.GetRequiredService<NotificationRepository>().RecordSentAsync(NotificationTypeName, $"{accountName} via {providerType}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The break-glass sign-in alert for {Account} could not be sent.", accountName);
            }
        });

    /// <summary>The plain-text alert. <paramref name="auditLogPage"/> is SiteLinks.Describe's "Admin > Audit Log" or "Admin > Audit Log (https://...)".</summary>
    public static string BuildBody(string accountName, string providerType, string? sourceIpAddress, DateTime signedInUtc, string auditLogPage) =>
        $"""
        The break-glass account {accountName} signed in to BlueTrack.

        When (UTC): {signedInUtc:yyyy-MM-dd HH:mm:ss}
        Sign-in method: {providerType}
        From: {sourceIpAddress ?? "unknown"}

        If this wasn't a planned emergency use, investigate now: check {auditLogPage} (event BreakGlassLogon) and the account's credential in CyberArk.
        """;
}
