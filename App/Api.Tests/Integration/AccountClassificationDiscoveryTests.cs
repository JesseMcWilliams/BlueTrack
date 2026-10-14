using BlueTrack.Api.AdDiscovery;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-233: account classification is global state (one settings row, one rule list), so every test class that
/// changes it runs in this collection, alone.
/// </summary>
[CollectionDefinition("AccountClassification", DisableParallelization = true)]
public sealed class AccountClassificationCollection;

/// <summary>
/// D-233: discovery with classification on, through the fake directory: the attributes are read in batches,
/// owners outside the discovered set are looked up and stored, every rule type classifies, a manual override
/// survives the next discovery run, and changing a rule reclassifies with no directory at all. The test's rules
/// only match its own uniquely named accounts; the settings are put back and everything added is removed.
/// </summary>
[Collection("AccountClassification")]
public class AccountClassificationDiscoveryTests
{
    private sealed record Row(string SamAccountName, string? AccountClass, string? ClassSource, string? OwnerSamAccountName,
        string? OwnerDisplayName, string? OwnerSource, string? EmployeeId, string? DirectoryAttributes);

    private static DirectoryUser User(string sam, string? employeeId = null, string? ou = "Users", Dictionary<string, string>? attributes = null) =>
        new(sam, $"CN={sam},OU={ou},DC=ct,DC=local", null, $"Display {sam}", true)
        {
            UserPrincipalName = $"{sam}@ct.local",
            EmployeeId = employeeId,
            Attributes = attributes ?? []
        };

    [Fact]
    public async Task Discovery_ReadsAttributes_LooksUpOwners_ClassifiesByEveryRuleType_KeepsOverrides_AndReclassifiesOnRuleChange()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var domain = $"ct-cls-{id}.local";
        var group = $"CT_ClsGroup_{id}";
        var factory = new TestDbConnectionFactory();
        var repository = new AccountClassificationRepository(factory);
        var reclassifier = new AccountClassificationService(repository);
        var userKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var originalSettings = await repository.GetSettingsAsync();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var ruleKeys = new List<int>();
        int? accessGroupKey = null;

        // The group's members; the person p{id} and the two amb owners are in no inventoried group.
        string person = $"p{id}", admin = $"t0{id}-p{id}", svc = $"svc{id}", ops = $"ops{id}", amb = $"amb{id}", plain = $"plain{id}";
        var members = new[]
        {
            User(admin, attributes: new() { ["adminCount"] = "1" }),
            User(svc, employeeId: $"E{id}", attributes: new() { ["extensionAttribute9"] = $"Svc{id}" }),
            User(ops, ou: $"Ops{id}"),
            User(amb, employeeId: $"A{id}", attributes: new() { ["extensionAttribute9"] = $"Svc{id}" }),
            User(plain)
        };
        var others = new[] { User(person, employeeId: $"E{id}"), User($"amb1{id}", employeeId: $"A{id}"), User($"amb2{id}", employeeId: $"A{id}") };
        var directory = new FakeAdDirectory(new() { [domain] = new() { [group] = members } }, new() { [domain] = others });

        try
        {
            await connection.ExecuteAsync(
                "INSERT INTO web.ldap_config (DomainName, IsEnabled, UseTrustedConnection, UseSsl) VALUES (@domain, 1, 1, 0)", new { domain });
            accessGroupKey = await connection.QuerySingleAsync<int>("""
                INSERT INTO web.dim_access_group (GroupName, GroupIdentifier, BaseRiskScore) OUTPUT inserted.AccessGroupKey VALUES (@group, @group, 50)
                """, new { group });

            await repository.SaveSettingsAsync(new AccountClassificationSettings
            {
                IsEnabled = true, DefaultAccountClass = "Unknown", OwnerByEmployeeId = true, EmployeeIdAttribute = "employeeID"
            }, userKey);
            async Task<int> AddRule(SaveAccountClassificationRuleRequest request)
            {
                var (error, rule) = AccountClassifier.Validate(request);
                Assert.Null(error);
                var key = await repository.CreateRuleAsync(rule!, userKey);
                ruleKeys.Add(key);
                return key;
            }
            var attributeRule = await AddRule(new() { RuleType = "Attribute", AttributeName = "extensionAttribute9", Operator = "Equals", Value = $"Svc{id}", AccountClass = "Secondary" });
            var nameRule = await AddRule(new() { RuleType = "Name", Operator = "StartsWith", Value = $"t0{id}-", AccountClass = "Admin" });
            var ouRule = await AddRule(new() { RuleType = "OU", Operator = "Contains", Value = $"OU=Ops{id}", AccountClass = "Admin" });

            var service = new AdAccountDiscoveryService(new LdapConfigRepository(factory), directory, new AccessGroupRepository(factory),
                new DiscoveredAccountRepository(factory), repository, reclassifier, NullLogger<AdAccountDiscoveryService>.Instance);
            await service.RunAsync();

            async Task<Dictionary<string, Row>> RowsAsync() =>
                (await connection.QueryAsync<Row>("""
                    SELECT SamAccountName, AccountClass, ClassSource, OwnerSamAccountName, OwnerDisplayName, OwnerSource, EmployeeId, DirectoryAttributes
                    FROM web.discovered_account WHERE DomainName = @domain
                    """, new { domain })).ToDictionary(r => r.SamAccountName, StringComparer.OrdinalIgnoreCase);

            var rows = await RowsAsync();
            Assert.Equal(("Admin", "Rule", person, $"Display {person}", "Name"),
                (rows[admin].AccountClass, rows[admin].ClassSource, rows[admin].OwnerSamAccountName, rows[admin].OwnerDisplayName, rows[admin].OwnerSource));
            Assert.Equal(("Secondary", person, "EmployeeId"), (rows[svc].AccountClass, rows[svc].OwnerSamAccountName, rows[svc].OwnerSource));
            Assert.Equal(("Admin", null, null), (rows[ops].AccountClass, rows[ops].OwnerSamAccountName, rows[ops].OwnerSource));
            Assert.Equal(("Secondary", null, "Ambiguous"), (rows[amb].AccountClass, rows[amb].OwnerSamAccountName, rows[amb].OwnerSource));
            Assert.Equal(("Unknown", "Default"), (rows[plain].AccountClass, rows[plain].ClassSource));
            Assert.Equal($"E{id}", rows[svc].EmployeeId);
            Assert.Contains("\"adminCount\":\"1\"", rows[admin].DirectoryAttributes);

            // The owners that aren't discovered were stored for reclassifying without LDAP.
            var lookups = (await connection.QueryAsync<string>("SELECT SamAccountName FROM web.ad_owner_lookup WHERE DomainName = @domain ORDER BY SamAccountName", new { domain })).ToList();
            Assert.Equal(new[] { $"amb1{id}", $"amb2{id}", person }.Order(StringComparer.OrdinalIgnoreCase), lookups, StringComparer.OrdinalIgnoreCase);

            // Bounded: one attribute read, one owner-name lookup and one employee-ID lookup for the domain.
            Assert.Equal([("sAMAccountName", 5), ("sAMAccountName", 1), ("employeeID", 2)],
                directory.FindCalls.Where(c => c.Domain == domain).Select(c => (c.Attribute, c.ValueCount)).ToList());

            // A manual override survives the next discovery run.
            var plainKey = await connection.QuerySingleAsync<int>("SELECT DiscoveredAccountKey FROM web.discovered_account WHERE DomainName = @domain AND SamAccountName = @plain", new { domain, plain });
            Assert.True((await repository.SaveOverrideAsync(plainKey, "Secondary", person, userKey)).Found);
            await service.RunAsync();
            rows = await RowsAsync();
            Assert.Equal(("Secondary", "Manual", person, "Manual"),
                (rows[plain].AccountClass, rows[plain].ClassSource, rows[plain].OwnerSamAccountName, rows[plain].OwnerSource));

            // Changing a rule reclassifies from what is stored -- no directory involved.
            var callsBefore = directory.FindCalls.Count;
            var (_, disabledOu) = AccountClassifier.Validate(new() { RuleType = "OU", Operator = "Contains", Value = $"OU=Ops{id}", AccountClass = "Admin", IsEnabled = false });
            Assert.True(await repository.UpdateRuleAsync(ouRule, disabledOu!, userKey));
            await reclassifier.ReclassifyAsync();
            rows = await RowsAsync();
            Assert.Equal(("Unknown", "Default"), (rows[ops].AccountClass, rows[ops].ClassSource));
            Assert.Equal(callsBefore, directory.FindCalls.Count);

            // Classification off: only the override is left.
            await repository.SaveSettingsAsync(new AccountClassificationSettings { IsEnabled = false }, userKey);
            await reclassifier.ReclassifyAsync();
            rows = await RowsAsync();
            Assert.Null(rows[admin].AccountClass);
            Assert.Equal("Secondary", rows[plain].AccountClass);
            _ = (attributeRule, nameRule);
        }
        finally
        {
            await repository.SaveSettingsAsync(originalSettings, userKey);
            await connection.ExecuteAsync("""
                DELETE m FROM web.discovered_account_access_group_map m JOIN web.discovered_account d ON d.DiscoveredAccountKey = m.DiscoveredAccountKey
                WHERE d.DomainName = @domain;
                DELETE FROM web.discovered_account WHERE DomainName = @domain;
                DELETE FROM web.ad_owner_lookup WHERE DomainName = @domain;
                DELETE FROM web.ldap_config WHERE DomainName = @domain;
                DELETE FROM web.dim_access_group WHERE AccessGroupKey = @accessGroupKey;
                DELETE FROM web.account_classification_rule WHERE AccountClassificationRuleKey IN @ruleKeys;
                """, new { domain, accessGroupKey, ruleKeys });
            await reclassifier.ReclassifyAsync();
        }
    }

    [Fact]
    public async Task MoveRule_SwapsWithinItsType_AndStopsAtTheEnds()
    {
        var factory = new TestDbConnectionFactory();
        var repository = new AccountClassificationRepository(factory);
        var userKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var id = Guid.NewGuid().ToString("N")[..8];
        var keys = new List<int>();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        try
        {
            foreach (var n in new[] { 1, 2 })
            {
                var (_, rule) = AccountClassifier.Validate(new() { RuleType = "Name", Operator = "StartsWith", Value = $"mv{id}-{n}", AccountClass = "Admin", IsEnabled = false });
                keys.Add(await repository.CreateRuleAsync(rule!, userKey));
            }
            List<int> Order(IEnumerable<AccountClassificationRule> rules) => rules.Where(r => keys.Contains(r.AccountClassificationRuleKey)).Select(r => r.AccountClassificationRuleKey).ToList();

            Assert.Equal(keys, Order(await repository.GetRulesAsync()));
            Assert.True(await repository.MoveRuleAsync(keys[1], up: true, userKey));
            Assert.Equal([keys[1], keys[0]], Order(await repository.GetRulesAsync()));
            var firstNameRule = (await repository.GetRulesAsync()).First(r => r.RuleType == "Name").AccountClassificationRuleKey;
            Assert.False(await repository.MoveRuleAsync(firstNameRule, up: true, userKey));
            Assert.False(await repository.MoveRuleAsync(int.MaxValue, up: true, userKey));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.account_classification_rule WHERE AccountClassificationRuleKey IN @keys", new { keys });
        }
    }
}
