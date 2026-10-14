using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-233: Admin > Account Classification (ManageDiscoveredAccounts) and the Discovered Accounts report's class
/// columns, Class filter, export and per-row override. Seeds one discovered account per test with a unique
/// domain; its rules only match that account. Settings are put back afterwards.
/// </summary>
[Collection("AccountClassification")]
public class AccountClassificationControllerTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private const string Base = "/api/admin/account-classification";
    private readonly BlueTrackWebApplicationFactory _factory;

    public AccountClassificationControllerTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private sealed record ReportRow(int DiscoveredAccountKey, string SamAccountName, string DomainName, string? AccountClass, string? ClassSource,
        int? ClassRuleKey, string? ClassRuleType, string? ClassRuleOperator, string? ClassRuleValue, string? OwnerSamAccountName, string? OwnerSource,
        string? ClassOverride, string? OwnerOverrideSamAccountName, string? OverrideByName);

    private static async Task<int> AuditCountAsync(string entityName, string entityKey)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<int>("""
            SELECT COUNT(*) FROM web.audit_event e
            JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey AND t.EventTypeName = 'AccountClassification'
            WHERE e.EntityName = @entityName AND e.EntityKey = @entityKey
            """, new { entityName, entityKey });
    }

    private static async Task<ReportRow?> ReportRowAsync(HttpClient client, string domain, string? accountClass = null)
    {
        var query = accountClass is null ? "" : $"&accountClass={accountClass}";
        var rows = await client.GetFromJsonAsync<List<ReportRow>>($"/api/reports/discovered-accounts?pageSize=500{query}");
        return rows!.SingleOrDefault(r => r.DomainName == domain);
    }

    [Theory]
    [InlineData("GET", Base)]
    [InlineData("PUT", Base + "/settings")]
    [InlineData("POST", Base + "/rules")]
    [InlineData("PUT", Base + "/rules/999999999")]
    [InlineData("DELETE", Base + "/rules/999999999")]
    [InlineData("POST", Base + "/rules/999999999/move")]
    [InlineData("POST", Base + "/reclassify")]
    [InlineData("PUT", "/api/reports/discovered-accounts/999999999/classification")]
    public async Task WithoutManageDiscoveredAccounts_IsForbidden(string method, string path)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method != "GET" && method != "DELETE") request.Content = JsonContent.Create(new { });
        var response = await Client("TestUser.Viewer").SendAsync(request);
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Get_ReturnsSettingsRulesAndTheAllowList()
    {
        var body = (await Client("TestUser.Admin").GetFromJsonAsync<JsonObject>(Base))!;
        Assert.NotNull(body["settings"]);
        Assert.NotNull(body["rules"]);
        var allowed = body["allowedAttributes"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("adminCount", allowed);
        Assert.Contains("extensionAttribute15", allowed);
        Assert.DoesNotContain("userAccountControl", allowed);
    }

    [Theory]
    [InlineData("""{"ruleType":"Attribute","attributeName":"userAccountControl","operator":"Equals","value":"512","accountClass":"Admin"}""", "Attribute must be one of")]
    [InlineData("""{"ruleType":"Attribute","attributeName":"adminCount)(objectClass=*","operator":"Equals","value":"1","accountClass":"Admin"}""", "Attribute must be one of")]
    [InlineData("""{"ruleType":"Name","operator":"StartsWith","value":"adm.","accountClass":"Owner"}""", "Class must be")]
    [InlineData("""{"ruleType":"OU","operator":"StartsWith","value":"OU=x","accountClass":"Admin"}""", "Operator must be one of")]
    public async Task InvalidRule_Is400_AndNothingIsSaved(string json, string expected)
    {
        var admin = Client("TestUser.Admin");
        var before = (await admin.GetFromJsonAsync<JsonObject>(Base))!["rules"]!.AsArray().Count;
        var response = await admin.PostAsync($"{Base}/rules", new StringContent(json, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, (await response.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!.GetValue<string>());
        Assert.Equal(before, (await admin.GetFromJsonAsync<JsonObject>(Base))!["rules"]!.AsArray().Count);
    }

    [Fact]
    public async Task InvalidSettingsAndClassFilter_Are400()
    {
        var admin = Client("TestUser.Admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{Base}/settings", new { isEnabled = true, defaultAccountClass = "Admin" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync($"{Base}/settings", new { isEnabled = true, employeeIdAttribute = "mail" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/reports/discovered-accounts?accountClass=Boss")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/reports/discovered-accounts/999999999/classification", new { accountClass = "Boss" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PutAsJsonAsync("/api/reports/discovered-accounts/999999999/classification", new { accountClass = "Admin" })).StatusCode);
    }

    [Fact]
    public async Task RuleChanges_Reclassify_AreAudited_AndTheOverrideWinsUntilCleared()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var domain = $"ct-cc-{id}.local";
        var sam = $"adm{id}.jsmith";
        var admin = Client("TestUser.Admin");
        var original = (await admin.GetFromJsonAsync<JsonObject>(Base))!["settings"]!;
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var key = await connection.QuerySingleAsync<int>("""
            INSERT INTO web.discovered_account (DomainName, SamAccountName, DistinguishedName, DisplayName, IsEnabled, DirectoryAttributes)
            OUTPUT inserted.DiscoveredAccountKey
            VALUES (@domain, @sam, 'CN=' + @sam + ',OU=Admins,DC=ct,DC=local', 'J Smith (admin)', 1, '{"adminCount":"1"}');
            INSERT INTO web.ad_owner_lookup (DomainName, SamAccountName, DisplayName, IsEnabled) VALUES (@domain, @owner, 'J Smith', 1);
            """, new { domain, sam, owner = "jsmith" });
        int? ruleKey = null;
        try
        {
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Base}/settings",
                new { isEnabled = true, defaultAccountClass = "Unknown", ownerByEmployeeId = false, employeeIdAttribute = "employeeID" })).StatusCode);
            Assert.Equal(("Unknown", "Default"), (await ReportRowAsync(admin, domain)) is { } r0 ? (r0.AccountClass, r0.ClassSource) : default);

            // Add a name rule: the account is Admin at once, with its owner from the stripped name.
            var created = await admin.PostAsJsonAsync($"{Base}/rules",
                new { ruleType = "Name", attributeName = "sAMAccountName", @operator = "StartsWith", value = $"adm{id}.", accountClass = "Admin", isEnabled = true });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            ruleKey = (await created.Content.ReadFromJsonAsync<JsonObject>())!["accountClassificationRuleKey"]!.GetValue<int>();
            var row = (await ReportRowAsync(admin, domain, "Admin"))!;
            Assert.Equal(("Admin", "Rule", ruleKey, "Name", "StartsWith", $"adm{id}.", "jsmith", "Name"),
                (row.AccountClass, row.ClassSource, row.ClassRuleKey, row.ClassRuleType, row.ClassRuleOperator, row.ClassRuleValue, row.OwnerSamAccountName, row.OwnerSource));
            Assert.Null(await ReportRowAsync(admin, domain, "Secondary"));
            Assert.Equal(1, await AuditCountAsync("account_classification_rule", ruleKey.ToString()!));

            // The export carries the class, owner and matched-by columns, with the Class filter.
            var csv = ReportExportTests.ParseCsv(await (await admin.GetAsync("/api/reports/discovered-accounts/export?format=csv&accountClass=Admin")).Content.ReadAsByteArrayAsync());
            var exported = csv.Skip(1).Single(r => r[1] == domain);
            Assert.Equal(["Admin", "jsmith (J Smith)", $"Name rule: sAMAccountName starts with \"adm{id}.\"; owner by name"], exported.Skip(2).Take(3));

            // Disable it: back to the default.
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync($"{Base}/rules/{ruleKey}",
                new { ruleType = "Name", @operator = "StartsWith", value = $"adm{id}.", accountClass = "Admin", isEnabled = false })).StatusCode);
            Assert.Equal("Unknown", (await ReportRowAsync(admin, domain))!.AccountClass);
            Assert.Equal(2, await AuditCountAsync("account_classification_rule", ruleKey.ToString()!));

            // A manual override wins and is audited; clearing it goes back to the rules.
            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/reports/discovered-accounts/{key}/classification",
                new { accountClass = "Secondary", ownerSamAccountName = $"{id}.jsmith" })).StatusCode);
            row = (await ReportRowAsync(admin, domain, "Secondary"))!;
            Assert.Equal(("Secondary", "Manual", "Secondary", $"{id}.jsmith", "Manual"), (row.AccountClass, row.ClassSource, row.ClassOverride, row.OwnerSamAccountName, row.OwnerSource));
            Assert.NotNull(row.OverrideByName);
            Assert.Equal(1, await AuditCountAsync("discovered_account", key.ToString()));

            Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/reports/discovered-accounts/{key}/classification",
                new { accountClass = (string?)null, ownerSamAccountName = (string?)null })).StatusCode);
            row = (await ReportRowAsync(admin, domain))!;
            Assert.Equal(("Unknown", "Default", null), (row.AccountClass, row.ClassSource, row.ClassOverride));

            Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"{Base}/rules/{ruleKey}")).StatusCode);
            Assert.Equal(3, await AuditCountAsync("account_classification_rule", ruleKey.ToString()!));
            ruleKey = null;
        }
        finally
        {
            await admin.PutAsJsonAsync($"{Base}/settings", original);
            if (ruleKey is not null) await admin.DeleteAsync($"{Base}/rules/{ruleKey}");
            await connection.ExecuteAsync("""
                DELETE FROM web.discovered_account WHERE DomainName = @domain;
                DELETE FROM web.ad_owner_lookup WHERE DomainName = @domain;
                """, new { domain });
        }
    }
}
