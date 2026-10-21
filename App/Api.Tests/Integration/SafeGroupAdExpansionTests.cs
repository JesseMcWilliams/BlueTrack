using BlueTrack.Api.AdDiscovery;
using BlueTrack.Api.CyberArkDirect;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-281: the Safe-member groups' AD read, against a fake directory and LDAP domains added to the test database for
/// the test: a Self-Hosted group by its DN in the domain the DN names, a Privilege Cloud group by name in every domain.
/// </summary>
public class SafeGroupAdExpansionTests
{
    private static DirectoryUser User(string sam) => new(sam, $"CN={sam},DC=x", null, sam.ToUpperInvariant(), true);

    private static PvwaSnapshot Snapshot()
    {
        var all = new Dictionary<string, bool> { ["listAccounts"] = true };
        return new PvwaSnapshot
        {
            Members =
            {
                new PvwaMember(1, "S", 30, "Vault Admins", true, null, all),
                new PvwaMember(1, "S", 33, "Gone", true, null, all),
                new PvwaMember(1, "S", 32, "Vault Only", true, null, all)
            },
            Groups =
            {
                new PvwaGroup(30, "Vault Admins", "Directory", "\\", null, []) { Dn = "CN=Vault Admins,OU=G,DC=Company,DC=com" },
                new PvwaGroup(31, "Not A Member", "Directory", "\\", null, []) { Dn = "CN=Not A Member,DC=Company,DC=com" },
                new PvwaGroup(32, "Vault Only", "Vault", "\\", null, []),
                new PvwaGroup(33, "Gone", "Directory", "\\", null, []) { Dn = "CN=Gone,DC=Company,DC=com" }
            }
        };
    }

    [Fact]
    public async Task ReadsEachSafeGroup_ByDnInItsDomain_OrByNameEverywhere_AndSaysWhatWentWrong()
    {
        var token = $"adx{Guid.NewGuid():N}"[..12];
        string[] domains = ["broken.example", "company.com", "other.example"];
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("INSERT INTO web.ldap_config (DomainName, IsEnabled, UseTrustedConnection, UseSsl) VALUES (@domain, 1, 1, 0)",
            domains.Select(domain => new { domain }));
        await connection.ExecuteAsync("""
            DECLARE @src INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
            INSERT INTO dim_safe (SourceSystemKey, SafeUrlId, SafeName) VALUES (@src, @token + '_Safe', @token + '_Safe');
            DECLARE @safeKey INT = SCOPE_IDENTITY();
            INSERT INTO dim_group (SourceSystemKey, SourceGroupId, GroupName) VALUES (@src, @token + '-G', @token + ' Admins');
            INSERT INTO fact_safe_entitlement (SafeKey, MemberType, GroupKey, SnapshotDate) VALUES (@safeKey, 'Group', SCOPE_IDENTITY(), CAST(SYSUTCDATETIME() AS DATE));
            """, new { token });
        try
        {
            var directory = new FakeAdDirectory(new()
            {
                ["company.com"] = new() { ["CN=Vault Admins,OU=G,DC=Company,DC=com"] = [User("ann"), User("bob")] },
                ["other.example"] = new() { [$"{token} Admins"] = [User("cid")], ["CN=Vault Admins,OU=G,DC=Company,DC=com"] = [User("wrong")] }
            }, failingDomain: "broken.example");
            var factory = new TestDbConnectionFactory();
            var service = new SafeGroupAdExpansionService(new LdapConfigRepository(factory), directory, new SelfHostedDirectRepository(factory),
                NullLogger<SafeGroupAdExpansionService>.Instance);

            var expansion = (await service.ExpandAsync(Snapshot(), CancellationToken.None))!;

            // The DN names company.com, so other.example (which has a group of that DN too) isn't asked; group 31 is on no Safe,
            // group 32 has no DN. Group 33 isn't in company.com.
            var reads = expansion.Reads.Where(r => r.Group.Source == "SELFHOSTED" || r.Group.Ref.StartsWith(token)).ToDictionary(r => r.Group.Ref);
            Assert.Equal(["30", "33", $"{token} Admins"], reads.Keys.Order());
            Assert.Equal(("Found", "company.com", (int?)2), (reads["30"].Status, reads["30"].DomainName, reads["30"].MemberCount));
            Assert.Equal(("NotFound", "Not found in company.com."), (reads["33"].Status, reads["33"].Message));
            // By name in every domain: broken.example can't be opened, company.com hasn't it, other.example has.
            Assert.Equal(("Found", "other.example"), (reads[$"{token} Admins"].Status, reads[$"{token} Admins"].DomainName));
            Assert.Equal(["ann", "bob"], expansion.Members.Where(m => m.Group.Ref == "30").Select(m => m.User.SamAccountName).Order());
            Assert.Equal("cid", Assert.Single(expansion.Members, m => m.Group.Ref == $"{token} Admins").User.SamAccountName);
        }
        finally
        {
            await connection.ExecuteAsync("""
                DELETE fse FROM fact_safe_entitlement fse JOIN dim_safe sf ON sf.SafeKey = fse.SafeKey WHERE sf.SafeUrlId = @token + '_Safe';
                DELETE FROM dim_safe WHERE SafeUrlId = @token + '_Safe';
                DELETE FROM dim_group WHERE SourceGroupId = @token + '-G';
                DELETE FROM web.ldap_config WHERE DomainName IN @domains;
                """, new { token, domains });
        }
    }

    [Fact]
    public async Task AGroupOnlyInADomainThatFails_IsFailed_AndNoDomainEnabledMeansNoRead()
    {
        var factory = new TestDbConnectionFactory();
        var directory = new FakeAdDirectory(new(), failingDomain: "company.com");
        var service = new SafeGroupAdExpansionService(new LdapConfigRepository(factory), directory, new SelfHostedDirectRepository(factory),
            NullLogger<SafeGroupAdExpansionService>.Instance);
        Assert.Null(await service.ExpandAsync(Snapshot(), CancellationToken.None));

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("INSERT INTO web.ldap_config (DomainName, IsEnabled, UseTrustedConnection, UseSsl) VALUES ('company.com', 1, 1, 0)");
        try
        {
            var expansion = (await service.ExpandAsync(Snapshot(), CancellationToken.None))!;

            var read = expansion.Reads.Single(r => r.Group.Ref == "30");
            Assert.Equal("Failed", read.Status);
            Assert.Contains("Domain controller unreachable", read.Message);
            Assert.Empty(expansion.Members);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.ldap_config WHERE DomainName = 'company.com'");
        }
    }

    [Fact]
    public async Task Standalone_ReadsPrivilegeCloudGroups_AndAppliesThemAtOnce()
    {
        // D-281: a site with Self-Hosted off has no pull, so the nightly service reads Privilege Cloud's Safe groups itself.
        var token = $"ads{Guid.NewGuid():N}"[..12];
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("INSERT INTO web.ldap_config (DomainName, IsEnabled, UseTrustedConnection, UseSsl) VALUES ('company.com', 1, 1, 0)");
        var groupKey = await connection.QuerySingleAsync<int>("""
            DECLARE @src INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
            INSERT INTO dim_safe (SourceSystemKey, SafeUrlId, SafeName) VALUES (@src, @token + '_Safe', @token + '_Safe');
            DECLARE @safeKey INT = SCOPE_IDENTITY();
            INSERT INTO dim_group (SourceSystemKey, SourceGroupId, GroupName) VALUES (@src, @token + '-G', @token + ' Admins');
            DECLARE @groupKey INT = SCOPE_IDENTITY();
            INSERT INTO fact_safe_entitlement (SafeKey, MemberType, GroupKey, SnapshotDate) VALUES (@safeKey, 'Group', @groupKey, CAST(SYSUTCDATETIME() AS DATE));
            SELECT @groupKey;
            """, new { token });
        try
        {
            var directory = new FakeAdDirectory(new() { ["company.com"] = new() { [$"{token} Admins"] = [User("ann"), User("bob")] } });
            var factory = new TestDbConnectionFactory();
            var service = new SafeGroupAdExpansionService(new LdapConfigRepository(factory), directory, new SelfHostedDirectRepository(factory),
                NullLogger<SafeGroupAdExpansionService>.Instance);

            var expansion = (await service.RunStandaloneAsync(CancellationToken.None))!;

            Assert.DoesNotContain(expansion.Reads, r => r.Group.Source == "SELFHOSTED");
            Assert.Equal(["ann", "bob"], (await connection.QueryAsync<string>(
                "SELECT SamAccountName FROM dbo.ad_group_member WHERE GroupKey = @groupKey ORDER BY SamAccountName", new { groupKey })).ToList());
            Assert.Equal(("Name", "Found", 2), await connection.QuerySingleAsync<(string, string, int)>(
                "SELECT MatchedBy, Status, MemberCount FROM dbo.ad_group_read WHERE GroupKey = @groupKey", new { groupKey }));
        }
        finally
        {
            await connection.ExecuteAsync("""
                DELETE FROM dbo.ad_group_member WHERE GroupKey = @groupKey; DELETE FROM dbo.ad_group_read WHERE GroupKey = @groupKey;
                DELETE FROM stg_shd_ad_group_reads; DELETE FROM stg_shd_ad_group_members;
                DELETE fse FROM fact_safe_entitlement fse JOIN dim_safe sf ON sf.SafeKey = fse.SafeKey WHERE sf.SafeUrlId = @token + '_Safe';
                DELETE FROM dim_safe WHERE SafeUrlId = @token + '_Safe';
                DELETE FROM dim_group WHERE GroupKey = @groupKey;
                DELETE FROM web.ldap_config WHERE DomainName = 'company.com';
                """, new { token, groupKey });
        }
    }

    [Theory]
    [InlineData("CN=G,OU=X,DC=Company,DC=com", "company.com")]
    [InlineData("CN=G,DC=nowhere,DC=test", "a.example|company.com")]
    public void DomainsFor_PicksTheDomainTheDnNames_ElseEveryDomain(string dn, string expected)
    {
        var configs = new[] { new LdapConfig { DomainName = "a.example" }, new LdapConfig { DomainName = "company.com" } };
        var domains = SafeGroupAdExpansionService.DomainsFor(new SafeMemberGroup("SELFHOSTED", "1", "G", dn), configs);
        Assert.Equal(expected, string.Join('|', domains.Select(d => d.DomainName)));
    }
}
