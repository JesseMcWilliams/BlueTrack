using Dapper;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Data;
using BlueTrack.Api.Notifications;

namespace BlueTrack.Api.Auth;

/// <summary>
/// D-285 (D-26, D-11): a rejected SAML assertion or failed OIDC sign-in is audit-logged as LogonFailed (attributed to
/// the 'BlueTrack sign-in (system)' user, as there is no signed-in user yet) and alerts the SignInRejected
/// notification's recipients. Never throws: the sign-in's own error response must still go out.
/// </summary>
public sealed class SignInRejectionRecorder(
    IDbConnectionFactory connectionFactory,
    AuditLogger auditLogger,
    SignInRejectedAlerter alerter,
    ILogger<SignInRejectionRecorder> logger)
{
    public const string SystemUserIdentifier = "S-1-0-0-signin";

    public async Task RecordAsync(string providerType, string reason, string? sourceIpAddress)
    {
        try
        {
            using var connection = connectionFactory.Create();
            var userKey = await connection.QuerySingleOrDefaultAsync<int?>(
                "SELECT TOP (1) UserKey FROM web.app_user WHERE ExternalIdentifier = @id", new { id = SystemUserIdentifier });
            if (userKey is null)
            {
                logger.LogWarning("Rejected {Provider} sign-in not audit-logged: the 'BlueTrack sign-in (system)' user is missing (script 49 seeds it).", providerType);
            }
            else
            {
                await auditLogger.LogAsync("LogonFailed", userKey.Value, "identity_provider", providerType,
                    detail: AuditLogger.ClipDetail($"{providerType} sign-in rejected: {reason}"));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogError(ex, "Rejected {Provider} sign-in could not be audit-logged.", providerType);
        }
        alerter.SendInBackground(providerType, reason, sourceIpAddress, DateTime.UtcNow);
    }
}

/// <summary>
/// D-285: emails the SignInRejected recipients, in the background like BreakGlassAlerter. At most one email per
/// <see cref="Window"/>: a burst (a misconfigured IdP, or someone probing) sends one email, and the next says how many
/// more there were. Every rejection is in the Audit Log either way.
/// </summary>
public sealed class SignInRejectedAlerter(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<SignInRejectedAlerter> logger)
{
    public const string NotificationTypeName = "SignInRejected";
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(15);

    private readonly object _gate = new();
    private DateTime _lastSentUtc = DateTime.MinValue;
    private int _suppressed;

    /// <summary>Whether this rejection gets an email now, and how many earlier ones went without one.</summary>
    public (bool Send, int EarlierSuppressed) Claim(DateTime nowUtc)
    {
        lock (_gate)
        {
            if (nowUtc - _lastSentUtc < Window)
            {
                _suppressed++;
                return (false, 0);
            }
            var earlier = _suppressed;
            _suppressed = 0;
            _lastSentUtc = nowUtc;
            return (true, earlier);
        }
    }

    public Task SendInBackground(string providerType, string reason, string? sourceIpAddress, DateTime whenUtc) =>
        Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var notifications = scope.ServiceProvider.GetRequiredService<NotificationRepository>();
                if (!await notifications.IsEnabledAsync(NotificationTypeName)) return;
                var (send, earlier) = Claim(timeProvider.GetUtcNow().UtcDateTime);
                if (!send) return;
                var recipients = await scope.ServiceProvider.GetRequiredService<NotificationRecipientResolver>().ResolveAsync(NotificationTypeName);
                if (recipients.Count == 0)
                {
                    logger.LogWarning("A {Provider} sign-in was rejected, but SignInRejected has no recipients -- no email sent.", providerType);
                    return;
                }
                var auditLog = SiteLinks.Describe(await scope.ServiceProvider.GetRequiredService<AppConfigRepository>().GetSiteUrlAsync(), SiteLinks.AuditLog);
                var body = BuildBody(providerType, reason, sourceIpAddress, whenUtc, earlier, auditLog);
                await scope.ServiceProvider.GetRequiredService<INotificationSender>().SendAsync(recipients, $"BlueTrack: a {providerType} sign-in was rejected", body);
                await notifications.RecordSentAsync(NotificationTypeName, $"{providerType}: {reason}");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The rejected {Provider} sign-in alert could not be sent.", providerType);
            }
        });

    public static string BuildBody(string providerType, string reason, string? sourceIpAddress, DateTime whenUtc, int earlierSuppressed, string auditLogPage) =>
        $"""
        A {providerType} sign-in to BlueTrack was rejected.

        When (UTC): {whenUtc:yyyy-MM-dd HH:mm:ss}
        Why: {reason}
        From: {sourceIpAddress ?? "unknown"}
        {(earlierSuppressed > 0 ? $"\n{earlierSuppressed} more rejection(s) since the last email (at most one email per {Window.TotalMinutes:0} minutes).\n" : "")}
        One now and then is usually a stale browser tab or a clock difference. Many, or one you can't explain, can mean the identity provider's configuration or certificate changed, or someone is trying forged responses: check {auditLogPage} (event LogonFailed) and Admin > Identity Providers.
        """;
}
