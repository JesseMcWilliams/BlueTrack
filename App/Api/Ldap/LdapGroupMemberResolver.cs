using System.DirectoryServices.AccountManagement;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Ldap;

/// <summary>
/// D-116: this app's first use of System.DirectoryServices -- a deliberate,
/// explicit reversal of AdminUsersController.cs's prior "deliberately does
/// NOT query AD/LDAP directly" stance, requested directly this time for
/// role-targeted notification email (Design_Notifications.md's updated
/// Proposed Workflow). Given a set of AD group identifiers (as stored in
/// web.identity_group_role_map.IdentityGroupName -- usually a SID for a real
/// domain group, sometimes a plain DevFakeAuth pseudo-group name that simply
/// won't resolve here and is silently skipped), expands each group's
/// membership recursively through nested groups and returns every distinct
/// member's own AD 'mail' attribute. Group-level mail aliases are
/// deliberately not used (the user's explicit choice) -- this reaches every
/// real person holding the role even if the group itself isn't mail-enabled.
///
/// LDAP lookups are entirely opt-in: returns an empty list with no attempt
/// at all unless web.ldap_config.IsEnabled and CredentialKey are both set.
/// Every exception (bind failure, an unresolvable group, network issues) is
/// left to the caller -- NotificationCheckBackgroundService wraps this in
/// its own try/catch so an LDAP problem degrades the recipient list rather
/// than blocking the rest of a notification send (Design_Notifications.md's
/// additive-union recipient model).
/// </summary>
public sealed class LdapGroupMemberResolver(LdapConfigRepository ldapConfigRepository, CredentialRepository credentialRepository)
{
    public async Task<IReadOnlyList<string>> ResolveMemberEmailsAsync(IReadOnlyList<string> groupIdentifiers)
    {
        if (groupIdentifiers.Count == 0)
        {
            return [];
        }

        var config = await ldapConfigRepository.GetAsync();
        if (!config.IsEnabled || config.CredentialKey is null)
        {
            return [];
        }

        var (username, password) = await credentialRepository.ResolveForUseAsync(config.CredentialKey.Value);

        using var context = new PrincipalContext(
            ContextType.Domain,
            string.IsNullOrWhiteSpace(config.DomainController) ? null : config.DomainController,
            string.IsNullOrWhiteSpace(config.SearchBase) ? null : config.SearchBase,
            username,
            password);

        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var identifier in groupIdentifiers)
        {
            using var group = FindGroup(context, identifier);
            if (group is null)
            {
                continue;
            }

            foreach (var member in group.GetMembers(recursive: true))
            {
                using (member)
                {
                    if (member is UserPrincipal user && !string.IsNullOrWhiteSpace(user.EmailAddress))
                    {
                        emails.Add(user.EmailAddress);
                    }
                }
            }
        }

        return emails.ToList();
    }

    /// <summary>A SID (the normal shape for a real domain group here, see GroupIdentifierExtractor) needs IdentityType.Sid; anything else falls back to SamAccountName/Name.</summary>
    private static GroupPrincipal? FindGroup(PrincipalContext context, string identifier)
    {
        try
        {
            return identifier.StartsWith("S-1-", StringComparison.Ordinal)
                ? GroupPrincipal.FindByIdentity(context, IdentityType.Sid, identifier)
                : GroupPrincipal.FindByIdentity(context, identifier);
        }
        catch (MultipleMatchesException)
        {
            return null;
        }
    }
}
