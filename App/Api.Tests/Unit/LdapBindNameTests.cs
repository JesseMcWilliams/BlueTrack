using BlueTrack.Api.Ldap;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-274: the name an explicit LDAP bind uses, a UPN where one can be built.</summary>
public class LdapBindNameTests
{
    private const string SearchBase = "OU=CyberArk,DC=company,DC=com";

    [Theory]
    // The lab's case: the Vault user name is longer than the 20-character sAMAccountName; LogonDomain gives the UPN.
    [InlineData("Automation_Admin_SaaS", "Company.com", "company.com", SearchBase, "Automation_Admin_SaaS@Company.com")]
    // LogonDomain as a NetBIOS name isn't a UPN suffix; the Address is next.
    [InlineData("svc.bind", "COMPANY", "company.com", SearchBase, "svc.bind@company.com")]
    // An Address that is an IP address, or a URL, isn't a domain; the Search Base is last.
    [InlineData("svc.bind", null, "10.1.2.3", SearchBase, "svc.bind@company.com")]
    [InlineData("svc.bind", "", "https://pvwa.company.com", SearchBase, "svc.bind@company.com")]
    // A name that already carries its domain is used as it is.
    [InlineData("svc.bind@other.example", "Company.com", "company.com", SearchBase, "svc.bind@other.example")]
    [InlineData("COMPANY\\svc.bind", "Company.com", "company.com", SearchBase, "COMPANY\\svc.bind")]
    // Nothing to build from: the name as it is.
    [InlineData("svc.bind", null, null, null, "svc.bind")]
    [InlineData("svc.bind", null, null, "OU=Users", "svc.bind")]
    [InlineData("  svc.bind ", " Company.com ", null, null, "svc.bind@Company.com")]
    public void BuildsAUpnWherePossible(string userName, string? logonDomain, string? address, string? searchBase, string expected) =>
        Assert.Equal(expected, LdapBindName.For(userName, logonDomain, address, searchBase));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void NoUserName_IsLeftAlone(string? userName) => Assert.Equal(userName, LdapBindName.For(userName, "Company.com", null, SearchBase));
}
