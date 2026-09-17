using System.DirectoryServices.AccountManagement;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Ldap;

/// <summary>
/// Shared PrincipalContext construction for a single web.ldap_config row --
/// factored out 2026-09-16 (AD Account Discovery feature) so
/// LdapGroupMemberResolver and the new AdAccountDiscoveryService build a
/// context the exact same way instead of duplicating the two-constructor-
/// overload subtlety documented below in two places.
/// </summary>
public sealed class LdapContextFactory(CredentialRepository credentialRepository)
{
    /// <summary>Null if this config isn't actually usable yet (disabled, or no bind method configured) -- never throws for that case, matching the existing "LDAP is entirely opt-in" behavior.</summary>
    public async Task<PrincipalContext?> TryCreateContextAsync(LdapConfig config)
    {
        if (!config.IsEnabled || (!config.UseTrustedConnection && config.CredentialKey is null))
        {
            return null;
        }

        var domainController = string.IsNullOrWhiteSpace(config.DomainController) ? null : config.DomainController;
        var searchBase = string.IsNullOrWhiteSpace(config.SearchBase) ? null : config.SearchBase;

        // Two distinct constructor overloads, not one call with nullable
        // username/password -- confirmed via a live probe against this
        // domain that the 3-arg (no-credentials) overload is what actually
        // binds as the ambient process identity; passing explicit nulls into
        // the 5-arg overload was never verified to behave the same way.
        if (config.UseTrustedConnection)
        {
            return new PrincipalContext(ContextType.Domain, domainController, searchBase);
        }

        var (username, password) = await credentialRepository.ResolveForUseAsync(config.CredentialKey!.Value);
        return new PrincipalContext(ContextType.Domain, domainController, searchBase, username, password);
    }
}
