using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-186: the decommission / ignored-safe name patterns, the two reports,
/// the account marker and the settings page's helper. The test creates
/// its own safes and accounts (unique names) and sets the patterns to match
/// only those, then removes them and restores the configuration.
/// </summary>
public class DecommissionPatternTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public DecommissionPatternTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private static async Task<JsonObject> PutPatternsAsync(HttpClient client, JsonObject config, string safeMode, string? safeValue,
        string accountMode, string? accountValue, string ignoreMode, string? ignoreValue, HttpStatusCode expected = HttpStatusCode.NoContent)
    {
        var body = config.DeepClone().AsObject();
        body["safeDecomMode"] = safeMode; body["safeDecomValue"] = safeValue;
        body["accountDecomMode"] = accountMode; body["accountDecomValue"] = accountValue;
        body["safeIgnoreMode"] = ignoreMode; body["safeIgnoreValue"] = ignoreValue;
        var response = await client.PutAsJsonAsync("/api/admin/configuration", body);
        Assert.Equal(expected, response.StatusCode);
        return body;
    }

    [Fact]
    public async Task Patterns_FlagSafesAndAccounts_ForTheReportsAndTheAccountList()
    {
        var tag = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var admin = Client("TestUser.Admin");
        var original = (await admin.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!;
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);

        // A flagged safe (DCM<tag>_Old) holding a live account and a deleted
        // one; the live account also exists in a normal safe (NEW<tag>_Safe);
        // and an account flagged by its own name (..._RET<tag>).
        await connection.ExecuteAsync("""
            DECLARE @src INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'DISCOVERY');
            DECLARE @stage INT = (SELECT StageKey FROM dim_blueprint_stage WHERE StageName = 'Discovered');
            DECLARE @status INT = (SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'Not Started');
            INSERT INTO dim_safe (SourceSystemKey, SafeUrlId, SafeName) VALUES (@src, @old, @old), (@src, @new, @new);
            DECLARE @oldKey INT = (SELECT SafeKey FROM dim_safe WHERE SafeUrlId = @old);
            DECLARE @newKey INT = (SELECT SafeKey FROM dim_safe WHERE SafeUrlId = @new);
            INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, UserName, Address, SafeKey, IsDeleted)
            VALUES (@src, @tag + '-1', @tag + '-live', 'svc_' + @tag, 'host-' + @tag + '.example.com', @oldKey, 0),
                   (@src, @tag + '-2', @tag + '-gone', 'old_' + @tag, 'host-' + @tag + '.example.com', @oldKey, 1),
                   (@src, @tag + '-3', @tag + '-moved', 'svc_' + @tag, 'HOST-' + @tag + '.example.com', @newKey, 0),
                   (@src, @tag + '-4', @tag + '-x_RET' + @tag, 'ret_' + @tag, NULL, @newKey, 0);
            INSERT INTO fact_account_progress (AccountKey, CurrentStageKey, CurrentStatusKey)
            SELECT AccountKey, @stage, @status FROM fact_account WHERE SourceAccountId LIKE @tag + '-%';
            """, new { tag, old = $"DCM{tag}_Old", @new = $"NEW{tag}_Safe" });

        try
        {
            await PutPatternsAsync(admin, original, "Prefix", $"dcm{tag}_", "Suffix", $"_ret{tag}", "Off", null);

            var safes = await admin.GetFromJsonAsync<JsonArray>("/api/reports/decom-safes");
            var safe = Assert.Single(safes!, s => s!["safeName"]!.GetValue<string>() == $"DCM{tag}_Old")!;
            Assert.Equal(1, safe["activeAccountCount"]!.GetValue<int>());
            Assert.Equal(1, safe["deletedAccountCount"]!.GetValue<int>());
            Assert.DoesNotContain(safes!, s => s!["safeName"]!.GetValue<string>() == $"NEW{tag}_Safe");

            var accounts = (await admin.GetFromJsonAsync<JsonArray>("/api/reports/decom-accounts"))!
                .Where(a => a!["accountName"]!.GetValue<string>().StartsWith(tag)).ToList();
            Assert.Equal(3, accounts.Count);
            var live = accounts.Single(a => a!["accountName"]!.GetValue<string>() == $"{tag}-live")!;
            Assert.True(live["inFlaggedSafe"]!.GetValue<bool>());
            Assert.False(live["isDeleted"]!.GetValue<bool>());
            Assert.Equal($"NEW{tag}_Safe", live["otherSafes"]!.GetValue<string>()); // same username + address, ignoring case
            Assert.True(accounts.Single(a => a!["accountName"]!.GetValue<string>() == $"{tag}-gone")!["isDeleted"]!.GetValue<bool>());
            var byName = accounts.Single(a => a!["accountName"]!.GetValue<string>() == $"{tag}-x_RET{tag}")!;
            Assert.True(byName["nameFlagged"]!.GetValue<bool>());
            Assert.False(byName["inFlaggedSafe"]!.GetValue<bool>());

            // The account list carries the marker.
            var list = await admin.GetFromJsonAsync<JsonArray>($"/api/account-progress?search=svc_{tag}&pageSize=50");
            var row = list!.Single(a => a!["accountName"]!.GetValue<string>() == $"{tag}-live")!;
            Assert.Equal($"DCM{tag}_Old", row["decomSafeName"]!.GetValue<string>());
            Assert.Equal($"NEW{tag}_Safe", row["decomOtherSafes"]!.GetValue<string>());
            Assert.Null(list!.Single(a => a!["accountName"]!.GetValue<string>() == $"{tag}-moved")!["decomSafeName"]);

            // An ignored safe drops out of the safes report.
            await PutPatternsAsync(admin, original, "Prefix", $"DCM{tag}_", "Off", null, "Suffix", "_old");
            Assert.DoesNotContain((await admin.GetFromJsonAsync<JsonArray>("/api/reports/decom-safes"))!,
                s => s!["safeName"]!.GetValue<string>() == $"DCM{tag}_Old");
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/admin/configuration", original);
            await connection.ExecuteAsync("""
                DELETE FROM fact_account_progress WHERE AccountKey IN (SELECT AccountKey FROM fact_account WHERE SourceAccountId LIKE @tag + '-%');
                DELETE FROM fact_account WHERE SourceAccountId LIKE @tag + '-%';
                DELETE FROM dim_safe WHERE SafeUrlId IN (@old, @new);
                """, new { tag, old = $"DCM{tag}_Old", @new = $"NEW{tag}_Safe" });
        }
    }

    [Fact]
    public async Task Settings_RejectUnusablePatterns_AndTheHelperShowsMatches()
    {
        var admin = Client("TestUser.Admin");
        var original = (await admin.GetFromJsonAsync<JsonObject>("/api/admin/configuration"))!;
        var regexSupported = original["regexSupported"]!.GetValue<bool>();

        try
        {
            await PutPatternsAsync(admin, original, "Prefix", "", "Off", null, "Off", null, HttpStatusCode.BadRequest);
            await PutPatternsAsync(admin, original, "Sometimes", "x", "Off", null, "Off", null, HttpStatusCode.BadRequest);
            // A bad regex (or any regex, where the server can't run it) is refused.
            await PutPatternsAsync(admin, original, "Regex", "([", "Off", null, "Off", null, HttpStatusCode.BadRequest);

            var test = await (await admin.PostAsJsonAsync("/api/admin/configuration/test-patterns", new
            {
                names = new[] { "DEL_Finance", "Finance_DECOM", "ZZ_Archive", "Finance" },
                safeDecom = new { mode = "Prefix", value = "del_" },
                accountDecom = new { mode = "Regex", value = "_decom$" },
                safeIgnore = new { mode = "Regex", value = "([" }
            })).Content.ReadFromJsonAsync<JsonObject>();
            var rows = test!["rows"]!.AsArray();
            Assert.Equal(["DEL_Finance", "Finance_DECOM", "ZZ_Archive", "Finance"], rows.Select(r => r!["name"]!.GetValue<string>()));
            Assert.True(rows[0]!["safeDecom"]!.GetValue<bool>());
            Assert.False(rows[3]!["safeDecom"]!.GetValue<bool>());
            Assert.Equal(regexSupported, rows[1]!["accountDecom"]!.GetValue<bool>());
            Assert.NotNull(test["errors"]!["SafeIgnore"]);
            Assert.DoesNotContain(rows, r => r!["safeIgnore"]!.GetValue<bool>());
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/admin/configuration", original);
        }
    }

    [Fact]
    public async Task Reports_AreOpenToAnySignedInUser_TheHelperIsNot()
    {
        var viewer = Client("TestUser.Viewer");
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/reports/decom-safes")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync("/api/reports/decom-accounts")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync("/api/admin/configuration/test-patterns", new { })).StatusCode);
    }
}
