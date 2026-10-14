using BlueTrack.Api.AdDiscovery;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-214: AD Account Discovery over two configured domains, with a fake
/// directory (only one real domain exists to test against). Each domain is
/// read on its own: a group found only in one domain gives that domain's
/// members, the same user name in both domains is two accounts, a user in two
/// groups of one domain is one account with both groups, and a domain that
/// fails doesn't stop the other. Everything the test adds is removed.
/// </summary>
public class AdAccountDiscoveryMultiDomainTests
{
    private sealed record Found(string DomainName, string SamAccountName, string? DisplayName, int GroupCount);

    private static DirectoryUser User(string sam, string domain) => new(sam, $"CN={sam},DC={domain}", null, $"{sam} ({domain})", true);

    [Fact]
    public async Task EachDomainIsReadOnItsOwn_AndAFailingDomainDoesNotStopTheOthers()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        string domainA = $"ct-a-{id}.local", domainB = $"ct-b-{id}.local", domainDown = $"ct-down-{id}.local";
        string groupA = $"CT_GroupA_{id}", groupShared = $"CT_GroupShared_{id}";
        var factory = new TestDbConnectionFactory();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var accessGroupKeys = new List<int>();
        try
        {
            foreach (var domain in new[] { domainA, domainB, domainDown })
            {
                await connection.ExecuteAsync(
                    "INSERT INTO web.ldap_config (DomainName, IsEnabled, UseTrustedConnection, UseSsl) VALUES (@domain, 1, 1, 0)", new { domain });
            }
            foreach (var group in new[] { groupA, groupShared })
            {
                accessGroupKeys.Add(await connection.QuerySingleAsync<int>("""
                    INSERT INTO web.dim_access_group (GroupName, GroupIdentifier, BaseRiskScore) OUTPUT inserted.AccessGroupKey
                    VALUES (@group, @group, 50)
                    """, new { group }));
            }

            var directory = new FakeAdDirectory(new()
            {
                // Domain A has both groups; svc_app is in both of them.
                [domainA] = new() { [groupA] = [User($"svc_app_{id}", "a"), User($"only_a_{id}", "a")], [groupShared] = [User($"svc_app_{id}", "a")] },
                // Domain B has only the shared group, with a user of the same name as one in A.
                [domainB] = new() { [groupShared] = [User($"svc_app_{id}", "b")] }
            }, failingDomain: domainDown);

            var classification = new AccountClassificationRepository(factory);
            var service = new AdAccountDiscoveryService(new LdapConfigRepository(factory), directory, new AccessGroupRepository(factory),
                new DiscoveredAccountRepository(factory), classification, new AccountClassificationService(classification),
                NullLogger<AdAccountDiscoveryService>.Instance);
            await service.RunAsync();

            var found = (await connection.QueryAsync<Found>("""
                SELECT d.DomainName, d.SamAccountName, d.DisplayName,
                       (SELECT COUNT(*) FROM web.discovered_account_access_group_map m WHERE m.DiscoveredAccountKey = d.DiscoveredAccountKey) AS GroupCount
                FROM web.discovered_account d WHERE d.DomainName IN @domains ORDER BY d.DomainName, d.SamAccountName
                """, new { domains = new[] { domainA, domainB, domainDown } })).ToList();

            Assert.Equal(
                [new Found(domainA, $"only_a_{id}", $"only_a_{id} (a)", 1), new Found(domainA, $"svc_app_{id}", $"svc_app_{id} (a)", 2),
                 new Found(domainB, $"svc_app_{id}", $"svc_app_{id} (b)", 1)],
                found);
        }
        finally
        {
            await connection.ExecuteAsync("""
                DELETE m FROM web.discovered_account_access_group_map m JOIN web.discovered_account d ON d.DiscoveredAccountKey = m.DiscoveredAccountKey
                WHERE d.DomainName IN @domains;
                DELETE FROM web.discovered_account WHERE DomainName IN @domains;
                DELETE FROM web.ldap_config WHERE DomainName IN @domains;
                DELETE FROM web.dim_access_group WHERE AccessGroupKey IN @accessGroupKeys;
                """, new { domains = new[] { domainA, domainB, domainDown }, accessGroupKeys });
        }
    }
}
