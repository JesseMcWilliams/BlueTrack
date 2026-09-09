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
        if (!config.IsEnabled || (!config.UseTrustedConnection && config.CredentialKey is null))
        {
            return [];
        }

        var domainController = string.IsNullOrWhiteSpace(config.DomainController) ? null : config.DomainController;
        var searchBase = string.IsNullOrWhiteSpace(config.SearchBase) ? null : config.SearchBase;

        // Two distinct constructor overloads, not one call with nullable
        // username/password -- confirmed via a live probe against this
        // domain that the 3-arg (no-credentials) overload is what actually
        // binds as the ambient process identity; passing explicit nulls into
        // the 5-arg overload was never verified to behave the same way.
        using var context = config.UseTrustedConnection
            ? new PrincipalContext(ContextType.Domain, domainController, searchBase)
            : await CreateCredentialedContextAsync(domainController, searchBase, config.CredentialKey!.Value);

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

    private async Task<PrincipalContext> CreateCredentialedContextAsync(string? domainController, string? searchBase, int credentialKey)
    {
        var (username, password) = await credentialRepository.ResolveForUseAsync(credentialKey);
        return new PrincipalContext(ContextType.Domain, domainController, searchBase, username, password);
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
