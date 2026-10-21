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
        return (await CreateAsync(config)).Context;
    }

    /// <summary>
    /// D-276: the domain's Test (Admin > Credentials &amp; LDAP), on unsaved values and whether or not the domain is
    /// enabled: binds the way discovery and group lookups would, and says as whom and to which domain controller.
    /// Never throws; the error is the directory's own message, which never carries the password.
    /// </summary>
    public async Task<LdapBindTestResult> TestBindAsync(LdapConfig config)
    {
        if (!config.UseTrustedConnection && config.CredentialKey is null)
            return new LdapBindTestResult(false, null, null, "Choose a bind account credential, or use a trusted connection.");
        string? bindName = null;
        try
        {
            var (context, name) = await CreateAsync(config);
            bindName = name;
            using (context)
            {
                // Reading ConnectedServer makes the context bind, to the Search Base when one is set.
                return new LdapBindTestResult(true, bindName, context.ConnectedServer, null);
            }
        }
        catch (Exception ex)
        {
            return new LdapBindTestResult(false, bindName, null, ex.Message);
        }
    }

    /// <summary>The context for a config, and the name an explicit bind uses (null for a trusted connection).</summary>
    private async Task<(PrincipalContext Context, string? BindName)> CreateAsync(LdapConfig config)
    {
        var domainController = string.IsNullOrWhiteSpace(config.DomainController) ? null : config.DomainController;
        var searchBase = string.IsNullOrWhiteSpace(config.SearchBase) ? null : config.SearchBase;

        // Two distinct constructor overloads, not one call with nullable
        // username/password -- confirmed via a live probe against this
        // domain that the 3-arg (no-credentials) overload is what actually
        // binds as the ambient process identity; passing explicit nulls into
        // the 5-arg overload was never verified to behave the same way.
        // D-198: UseSsl was saved but never applied, so ticking it changed
        // nothing. When set, bind over LDAPS (SecureSocketLayer, with Negotiate
        // for the credentials); when not, the original overloads are used
        // unchanged. Not yet verified against a live LDAPS domain controller.
        if (config.UseTrustedConnection)
        {
            return (config.UseSsl
                ? new PrincipalContext(ContextType.Domain, domainController, searchBase, ContextOptions.SecureSocketLayer | ContextOptions.Negotiate)
                : new PrincipalContext(ContextType.Domain, domainController, searchBase), null);
        }

        // D-274: bind with a UPN where one can be built from the account's LogonDomain, Address or the Search Base.
        var credential = await credentialRepository.ResolveWithAccountDetailsAsync(config.CredentialKey!.Value);
        var username = LdapBindName.For(credential.Username, credential.LogonDomain, credential.Address, searchBase);
        var password = credential.Password;
        return (config.UseSsl
            ? new PrincipalContext(ContextType.Domain, domainController, searchBase, ContextOptions.SecureSocketLayer | ContextOptions.Negotiate, username, password)
            : new PrincipalContext(ContextType.Domain, domainController, searchBase, username, password), username);
    }
}

/// <summary>D-276: how a domain's bind Test went. BindName: the name an explicit bind used (null for a trusted connection).</summary>
public sealed record LdapBindTestResult(bool Success, string? BindName, string? Server, string? Error);
