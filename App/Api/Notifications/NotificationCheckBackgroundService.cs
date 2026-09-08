using BlueTrack.Api.Data;
using BlueTrack.Api.Ldap;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// Design_Notifications.md, D-115: runs every registered INotificationCheck
/// once a day (this app's only other recurring-work precedent, the SQL
/// Agent Import/Load job, runs nightly -- matched in spirit, not literally
/// tied together, since that job is T-SQL-only and this needs a real SMTP
/// client). A background service inside BlueTrack.Api itself, not SQL
/// Server Database Mail -- the user's explicit choice, so SMTP settings
/// stay admin-UI-editable (web.notification_config) instead of living in
/// msdb's own system tables.
///
/// Each check gets its own scope (IServiceScopeFactory) since
/// NotificationRepository/INotificationCheck implementations depend on
/// Scoped services (IDbConnectionFactory-backed repositories) -- a
/// BackgroundService itself is a Singleton and can't depend on Scoped
/// services directly.
/// </summary>
public sealed class NotificationCheckBackgroundService(
    IServiceScopeFactory scopeFactory,
    ILogger<NotificationCheckBackgroundService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            try
            {
                await Task.Delay(CheckInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var checks = scope.ServiceProvider.GetServices<INotificationCheck>();
        var repository = scope.ServiceProvider.GetRequiredService<NotificationRepository>();
        var sender = scope.ServiceProvider.GetRequiredService<INotificationSender>();
        var ldapResolver = scope.ServiceProvider.GetRequiredService<LdapGroupMemberResolver>();

        foreach (var check in checks)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                var result = await check.EvaluateAsync();
                if (result is null)
                {
                    continue;
                }

                if (await repository.WasSentRecentlyAsync(check.NotificationTypeName, check.Cooldown))
                {
                    continue;
                }

                var recipients = new HashSet<string>(await repository.GetActiveRecipientEmailsAsync(), StringComparer.OrdinalIgnoreCase);

                // D-116: additive union, not a replacement -- a notification type
                // with no TargetRoleKey contributes nothing here, preserving
                // D-115's exact flat-list-only behavior.
                var (roleEmail, mappedGroups) = await repository.GetTargetRoleInfoAsync(check.NotificationTypeName);
                if (!string.IsNullOrWhiteSpace(roleEmail))
                {
                    recipients.Add(roleEmail);
                }

                try
                {
                    foreach (var email in await ldapResolver.ResolveMemberEmailsAsync(mappedGroups))
                    {
                        recipients.Add(email);
                    }
                }
                catch (Exception ex)
                {
                    // LDAP is opt-in and can fail independently of everything
                    // else (bind account misconfigured, domain unreachable) --
                    // degrades the recipient list rather than blocking the send.
                    logger.LogWarning(ex, "LDAP recipient resolution failed for {NotificationType} -- continuing with non-LDAP recipients.", check.NotificationTypeName);
                }

                if (recipients.Count == 0)
                {
                    logger.LogWarning("Notification {NotificationType} triggered but no recipients are configured -- not sent.", check.NotificationTypeName);
                    continue;
                }

                await sender.SendAsync(recipients.ToList(), result.Subject, result.Body);
                await repository.RecordSentAsync(check.NotificationTypeName, result.Detail);
            }
            catch (Exception ex)
            {
                // One check failing (e.g. SMTP unreachable) must not stop the
                // others from running, and must not crash this background
                // service -- it just tries again next cycle.
                logger.LogError(ex, "Notification check {NotificationType} failed.", check.NotificationTypeName);
            }
        }
    }
}
