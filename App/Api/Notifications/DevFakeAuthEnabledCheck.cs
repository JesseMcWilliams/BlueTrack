using Dapper;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// D-114/D-115: the same signal App.vue's in-app banner already uses
/// (identity_provider_config.ModifiedDate as the closest available
/// "enabled since" proxy -- not a dedicated one, see that column's own
/// comment on IdentityProviderDetail.cs). Queries web.identity_provider_config
/// directly rather than going through IdentityProviderRepository/
/// IdentityProviderDetail -- this only needs three columns, and that
/// repository's shape (SecretReference redaction, etc.) is for the admin
/// page, not this background check.
/// </summary>
public sealed class DevFakeAuthEnabledCheck(IDbConnectionFactory connectionFactory) : INotificationCheck
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    public string NotificationTypeName => "DevFakeAuthEnabledTooLong";
    public TimeSpan Cooldown => TimeSpan.FromDays(7);

    private sealed class ProviderState
    {
        public bool IsEnabled { get; init; }
        public DateTime? ModifiedDate { get; init; }
    }

    public async Task<NotificationCheckResult?> EvaluateAsync()
    {
        using var connection = connectionFactory.Create();
        const string sql = """
            SELECT IsEnabled, ModifiedDate
            FROM web.identity_provider_config
            WHERE ProviderType = 'DevFakeAuth'
            """;
        var row = await connection.QuerySingleOrDefaultAsync<ProviderState>(sql);

        if (row is null || !row.IsEnabled || row.ModifiedDate is null)
        {
            return null;
        }

        var enabledFor = DateTime.UtcNow - row.ModifiedDate.Value;
        if (enabledFor < StaleAfter)
        {
            return null;
        }

        var since = row.ModifiedDate.Value.ToString("yyyy-MM-dd");
        var body = $"DevFakeAuth has been enabled on this BlueTrack environment for over {StaleAfter.Days} days (since {since}). " +
            "It bypasses real authentication and should only be left on briefly during local development.";
        return new NotificationCheckResult(Subject: "BlueTrack: DevFakeAuth enabled too long", Body: body, Detail: $"ModifiedDate={since}");
    }
}
