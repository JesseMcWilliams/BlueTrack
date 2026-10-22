using BlueTrack.Api.Data;
using BlueTrack.Api.Ldap;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// Design_Notifications.md, D-115: runs every registered INotificationCheck
/// on a timer. D-222: every 5 minutes (it was daily), so an Import and Load
/// failure is emailed within about 5 minutes; the scheduled digests decide
/// for themselves whether they're due, and each check is skipped while its
/// type is disabled. A background service inside BlueTrack.Api itself, not SQL
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
    TimeProvider timeProvider,
    ILogger<NotificationCheckBackgroundService> logger) : BackgroundService
{
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await RunOnceAsync(stoppingToken);

            try
            {
                await Task.Delay(CheckInterval, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }
    }

    /// <summary>One cycle: every enabled check, each result sent and logged. Public for tests.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var checks = scope.ServiceProvider.GetServices<INotificationCheck>();
        var repository = scope.ServiceProvider.GetRequiredService<NotificationRepository>();
        var sender = scope.ServiceProvider.GetRequiredService<INotificationSender>();
        var recipientResolver = scope.ServiceProvider.GetRequiredService<NotificationRecipientResolver>();
        // D-294: the emails use the installation's number and date format as it is now.
        if (scope.ServiceProvider.GetService<AppConfigRepository>() is { } appConfig) await appConfig.RefreshFormatCultureAsync();

        foreach (var check in checks)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (!await repository.IsEnabledAsync(check.NotificationTypeName))
                {
                    continue;
                }

                foreach (var result in await check.EvaluateAsync())
                {
                    await DeliverAsync(check, result, repository, sender, recipientResolver);
                }
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

    private async Task DeliverAsync(INotificationCheck check, NotificationCheckResult result, NotificationRepository repository,
        INotificationSender sender, NotificationRecipientResolver recipientResolver)
    {
        if (result.DedupKey is null)
        {
            if (await repository.WasSentRecentlyAsync(check.NotificationTypeName, check.Cooldown))
            {
                return;
            }

            var recipients = result.Recipients ?? await recipientResolver.ResolveAsync(check.NotificationTypeName);
            if (recipients.Count == 0)
            {
                logger.LogWarning("Notification {NotificationType} triggered but no recipients are configured -- not sent.", check.NotificationTypeName);
                return;
            }

            await sender.SendAsync(recipients, result.Subject, result.Body, result.HtmlBody);
            await repository.RecordSentAsync(check.NotificationTypeName, result.Detail);
            return;
        }

        // D-222: claim first, so this run/day/week goes out once; release the
        // claim if it can't be sent, so the next cycle tries again.
        var claim = await repository.TryClaimAsync(check.NotificationTypeName, result.DedupKey, result.Detail);
        if (claim is null)
        {
            return;
        }

        try
        {
            var recipients = result.Recipients ?? await recipientResolver.ResolveAsync(check.NotificationTypeName);
            if (recipients.Count == 0)
            {
                logger.LogWarning("Notification {NotificationType} ({DedupKey}) triggered but no recipients are configured -- not sent.", check.NotificationTypeName, result.DedupKey);
                await repository.ReleaseClaimAsync(claim.Value);
                return;
            }

            await sender.SendAsync(recipients, result.Subject, result.Body, result.HtmlBody);
        }
        catch
        {
            await repository.ReleaseClaimAsync(claim.Value);
            throw;
        }
    }
}
