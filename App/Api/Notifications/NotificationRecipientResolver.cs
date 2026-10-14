using BlueTrack.Api.Data;
using BlueTrack.Api.Ldap;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// Who a notification type goes to: every active recipient (D-115), plus
/// the target role's NotificationEmail and the members of its mapped AD
/// groups (D-116). Moved out of NotificationCheckBackgroundService unchanged
/// (D-216) so the break-glass alert, sent at sign-in, uses the same list.
/// </summary>
public sealed class NotificationRecipientResolver(
    NotificationRepository repository,
    LdapGroupMemberResolver ldapResolver,
    ILogger<NotificationRecipientResolver> logger)
{
    public async Task<IReadOnlyList<string>> ResolveAsync(string notificationTypeName)
    {
        var recipients = new HashSet<string>(await repository.GetActiveRecipientEmailsAsync(), StringComparer.OrdinalIgnoreCase);

        // D-116: additive union, not a replacement -- a notification type
        // with no TargetRoleKey contributes nothing here, preserving
        // D-115's exact flat-list-only behavior.
        var (roleEmail, mappedGroups) = await repository.GetTargetRoleInfoAsync(notificationTypeName);
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
            logger.LogWarning(ex, "LDAP recipient resolution failed for {NotificationType} -- continuing with non-LDAP recipients.", notificationTypeName);
        }

        return recipients.ToList();
    }
}
