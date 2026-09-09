namespace BlueTrack.Api.Models;

/// <summary>
/// web.ldap_config (D-116) -- singleton, disabled by default. LDAP lookups
/// are skipped entirely (not attempted, not an error) until both IsEnabled
/// and CredentialKey are set -- see LdapGroupMemberResolver.
/// </summary>
public sealed class LdapConfig
{
    public int LdapConfigKey { get; init; }
    public bool IsEnabled { get; init; }
    public string? DomainController { get; init; }
    public string? SearchBase { get; init; }
    public bool UseSsl { get; init; }

    /// <summary>When true, binds as the app pool's own Windows identity (or the local computer account) instead of an explicit CredentialKey -- verified live against a real domain.</summary>
    public bool UseTrustedConnection { get; init; }
    public int? CredentialKey { get; init; }
    public string? CredentialName { get; init; }
}

public sealed class SaveLdapConfigRequest
{
    public bool IsEnabled { get; init; }
    public string? DomainController { get; init; }
    public string? SearchBase { get; init; }
    public bool UseSsl { get; init; }
    public bool UseTrustedConnection { get; init; }
    public int? CredentialKey { get; init; }
}
