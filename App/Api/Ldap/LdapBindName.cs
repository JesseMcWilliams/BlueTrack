using System.Net;

namespace BlueTrack.Api.Ldap;

/// <summary>
/// D-274: the name an explicit LDAP bind uses. A UPN where one can be built, since a Vault account's user name may not be
/// the AD logon name: sAMAccountName is cut at 20 characters, so a longer user name only binds as user@domain (the lab's
/// Automation_Admin_SaaS, 2026-10-21; Reference_Lessons-Learned.md).
/// </summary>
public static class LdapBindName
{
    /// <summary>
    /// A user name already holding a domain (user@domain, DOMAIN\user) is used as it is. Otherwise user@domain, the domain
    /// taken from the account's LogonDomain if it is a DNS name, else its Address if that is a domain name, else the
    /// LDAP domain's Search Base (DC=company,DC=com gives company.com). With none of them, the user name as it is.
    /// </summary>
    public static string? For(string? userName, string? logonDomain, string? address, string? searchBase)
    {
        if (string.IsNullOrWhiteSpace(userName)) return userName;
        var name = userName.Trim();
        if (name.Contains('@') || name.Contains('\\')) return name;

        var domain = DnsName(logonDomain) ?? DnsName(address) ?? FromSearchBase(searchBase);
        return domain is null ? name : $"{name}@{domain}";
    }

    /// <summary>The value if it looks like a DNS domain name (dotted, no IP address, port, path or space), else null.</summary>
    private static string? DnsName(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v) || !v.Contains('.') || v.StartsWith('.') || v.EndsWith('.')) return null;
        if (v.IndexOfAny([' ', ':', '/', '\\', '@']) >= 0 || IPAddress.TryParse(v, out _)) return null;
        return v;
    }

    private static string? FromSearchBase(string? searchBase)
    {
        if (string.IsNullOrWhiteSpace(searchBase)) return null;
        var parts = searchBase.Split(',')
            .Select(p => p.Trim())
            .Where(p => p.StartsWith("DC=", StringComparison.OrdinalIgnoreCase))
            .Select(p => p[3..].Trim())
            .Where(p => p.Length > 0)
            .ToList();
        return parts.Count >= 2 ? string.Join('.', parts) : null;
    }
}
