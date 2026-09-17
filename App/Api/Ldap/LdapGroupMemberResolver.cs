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
/// at all unless web.ldap_config.IsEnabled is set, and either
/// UseTrustedConnection or a CredentialKey is set. Every exception (bind
/// failure, an unresolvable group, network issues) is left to the caller --
/// NotificationCheckBackgroundService wraps this in its own try/catch so an
/// LDAP problem degrades the recipient list rather than blocking the rest of
/// a notification send (Design_Notifications.md's additive-union recipient
/// model).
///
/// D-118: UseTrustedConnection binds as the app pool's own Windows identity
/// (or the local computer account) instead of an explicit bind-account
/// credential -- confirmed live against a real domain that
/// PrincipalContext, given no explicit username/password, binds as whatever
/// identity the current process runs under and can expand real group
/// membership just as well as an explicit bind account.
///
/// Extended 2026-09-16 (AD Account Discovery feature, D-116 follow-up):
/// web.ldap_config went from a singleton to one row per domain. A group
/// identifier here carries no domain hint of its own, so every enabled
/// config is tried in turn per identifier until one binds it -- a SID only
/// ever resolves in the one domain it actually belongs to, so trying the
/// "wrong" domain's context first just returns null (not an error) and
/// falls through to the next, same as an unresolvable identifier always did.
/// </summary>
public sealed class LdapGroupMemberResolver(LdapConfigRepository ldapConfigRepository, LdapContextFactory contextFactory)
{
    public async Task<IReadOnlyList<string>> ResolveMemberEmailsAsync(IReadOnlyList<string> groupIdentifiers)
    {
        if (groupIdentifiers.Count == 0)
        {
            return [];
        }

        var configs = await ldapConfigRepository.GetEnabledAsync();
        if (configs.Count == 0)
        {
            return [];
        }

        var emails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var config in configs)
        {
            using var context = await contextFactory.TryCreateContextAsync(config);
            if (context is null)
            {
                continue;
            }

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
        }

        return emails.ToList();
    }

    /// <summary>A SID (the normal shape for a real domain group here, see GroupIdentifierExtractor) needs IdentityType.Sid; anything else falls back to SamAccountName/Name.</summary>
    internal static GroupPrincipal? FindGroup(PrincipalContext context, string identifier)
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
