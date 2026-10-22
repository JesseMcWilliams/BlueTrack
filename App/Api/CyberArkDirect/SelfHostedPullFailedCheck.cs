using System.Globalization;
using Dapper;
using BlueTrack.Api.Data;
using BlueTrack.Api.Notifications;
using BlueTrack.Api.Formatting;

namespace BlueTrack.Api.CyberArkDirect;

/// <summary>
/// D-250: a Self-Hosted Direct pull that Failed or was Rejected (dbo.cyberark_direct_pull), mailed when it happens.
/// Until now the first word was the nightly import failing at 02:00, half an hour after the 01:30 pull; this tells
/// the admin when it happened and what to do. One email per pull (the DedupKey is the pull). A pull still Started
/// after StuckAfter, which the next pull would mark Failed, is reported too. Pulls older than Lookback are ignored,
/// so a long mail outage drops the alert rather than sending a stale one.
/// </summary>
public sealed class SelfHostedPullFailedCheck(IDbConnectionFactory connectionFactory, AppConfigRepository appConfigRepository, TimeProvider timeProvider) : INotificationCheck
{
    public const string TypeName = "SelfHostedPullFailed";
    public static readonly TimeSpan StuckAfter = TimeSpan.FromHours(2);
    public static readonly TimeSpan Lookback = TimeSpan.FromHours(28);

    public string NotificationTypeName => TypeName;
    public TimeSpan Cooldown => TimeSpan.Zero;   // Unused: every result has a DedupKey.

    public static string DedupKeyFor(long pullKey) => "pull:" + pullKey.ToString(CultureInfo.InvariantCulture);

    public async Task<IReadOnlyList<NotificationCheckResult>> EvaluateAsync()
    {
        using var connection = connectionFactory.Create();
        var pulls = (await connection.QueryAsync<DirectPullRecord>("""
            SELECT p.PullKey, p.StartedAt, p.CompletedAt, p.Status, p.TriggeredBy, p.AcceptedChange, p.SafeCount, p.AccountCount,
                   p.UserCount, p.GroupCount, p.MemberCount, p.PlatformCount, p.Message,
                   b.SafeCount AS BaselineSafeCount, b.AccountCount AS BaselineAccountCount
            FROM dbo.cyberark_direct_pull p
            OUTER APPLY (SELECT TOP (1) SafeCount, AccountCount FROM dbo.cyberark_direct_pull
                         WHERE Status = N'Succeeded' AND PullKey < p.PullKey ORDER BY PullKey DESC) b
            WHERE p.StartedAt >= DATEADD(MINUTE, -@LookbackMinutes, SYSUTCDATETIME())
              AND (p.Status IN (N'Failed', N'Rejected')
                   OR (p.Status = N'Started' AND p.StartedAt < DATEADD(MINUTE, -@StuckMinutes, SYSUTCDATETIME())))
              AND NOT EXISTS (
                  SELECT 1 FROM web.notification_log nl
                  JOIN web.dim_notification_type nt ON nt.NotificationTypeKey = nl.NotificationTypeKey
                  WHERE nt.NotificationTypeName = @TypeName AND nl.DedupKey = CONCAT('pull:', p.PullKey))
            ORDER BY p.PullKey
            """, new { TypeName, LookbackMinutes = (int)Lookback.TotalMinutes, StuckMinutes = (int)StuckAfter.TotalMinutes })).ToList();

        if (pulls.Count == 0) return [];
        var siteUrl = await appConfigRepository.GetSiteUrlAsync();
        return pulls.Select(p => Build(p, siteUrl)).ToList();
    }

    private NotificationCheckResult Build(DirectPullRecord pull, string? siteUrl)
    {
        var verb = pull.Status switch { "Rejected" => "was rejected", "Started" => "has not finished", _ => "failed" };
        var email = new NotificationEmail($"BlueTrack: the Self-Hosted Direct pull {verb}", siteUrl);

        var facts = new List<(string, string)>
        {
            ("Started (server time)", NotificationEmail.ServerTime(pull.StartedAt, timeProvider)),
            ("Started by", pull.TriggeredBy),
            ("Outcome", pull.Status),
        };
        if (pull.SafeCount is not null)
        {
            facts.Add(("Safes", BeforeAndNow(pull.BaselineSafeCount, pull.SafeCount)));
            facts.Add(("Accounts", BeforeAndNow(pull.BaselineAccountCount, pull.AccountCount)));
        }
        facts.Add(("Pull", pull.PullKey.ToString(CultureInfo.InvariantCulture)));
        email.Facts(facts);

        var messages = (pull.Message ?? "").Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var message in messages) email.Paragraph(message);
        if (messages.Length == 0 && pull.Status != "Started") email.Paragraph("(no message recorded)");

        email.Paragraph(pull.Status switch
        {
            "Rejected" => "The pull read PVWA but wasn't used, because the number of Safes or accounts changed by more than the allowed percentage. The staged data is unchanged and tonight's import will fail until a pull succeeds. If the change is expected, use Accept the change and pull; if not, check the service user's Safe memberships first.",
            "Started" => "The pull may still be running or may have stopped with the application. The next pull marks it failed. Tonight's import will fail if no pull succeeds before it.",
            _ => "The staged data is unchanged and tonight's import will fail until a pull succeeds. Common causes: the credential was refused or its CP/CCP lookup failed, PVWA can't be reached from this server, or the PVWA address is wrong."
        });
        email.SeePage(SiteLinks.DataSources, "to see the pull and run it again");

        var detail = $"Pull {pull.PullKey} {verb}" + (string.IsNullOrEmpty(pull.Message) ? "" : $": {pull.Message}");
        return new NotificationCheckResult(
            Subject: $"BlueTrack: Self-Hosted Direct pull {verb}",
            Body: email.Text,
            Detail: detail,
            HtmlBody: email.Html,
            DedupKey: DedupKeyFor(pull.PullKey));
    }

    // D-294: counts in the installation's format culture.
    private static string BeforeAndNow(int? before, int? now) =>
        before is null ? ServerTextCulture.Count(now ?? 0) : $"{ServerTextCulture.Count(before.Value)} before, {ServerTextCulture.Count(now ?? 0)} now";
}
