using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-212 (Data Sources phase 4a): source systems for account inventory feeds,
/// the feed's validation, and real runs of its import. Each test uses its own
/// new source system, and removes it with its accounts and feed afterwards.
/// </summary>
public class AccountInventoryFeedTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public AccountInventoryFeedTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");
        return client;
    }

    private sealed record SourceResponse(int SourceSystemKey, string SourceSystemCode, string SourceSystemName);
    private sealed record KeyResponse(int DataFeedKey);
    private sealed record RunResponse(long DataFeedRunKey, string Outcome, int? TotalRows, int? ErrorRows, string? Summary, string? ErrorMessage);
    private sealed record StatusResponse(bool IsRunning);
    private sealed record AccountRow(string SourceAccountId, string AccountName, string? Address, string? UserName, bool IsDeleted, bool IsDeletedInSource, string? StageName, string? OwnerName);

    private static object Feed(string name, string folder, int? sourceSystemKey, string feedType = "AccountInventory", bool skipGroupRows = false) => new
    {
        displayName = name, feedType, folderPath = folder, fileNamePattern = "accounts.csv", isEnabled = false, displayOrder = 960, sourceSystemKey, skipGroupRows
    };

    private static async Task<SourceResponse> CreateSourceAsync(HttpClient client)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();
        var response = await client.PostAsJsonAsync("/api/admin/data-feeds/source-systems",
            new { sourceSystemCode = $"ct_{suffix}", sourceSystemName = $"Contract test source {suffix}" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<SourceResponse>())!;
    }

    private static async Task<List<AccountRow>> AccountsAsync(int sourceSystemKey)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return (await connection.QueryAsync<AccountRow>("""
            SELECT fa.SourceAccountId, fa.AccountName, fa.Address, fa.UserName, fa.IsDeleted, fa.IsDeletedInSource, s.StageName, p.OwnerName
            FROM dbo.fact_account fa
            LEFT JOIN dbo.fact_account_progress p ON p.AccountKey = fa.AccountKey
            LEFT JOIN dbo.dim_blueprint_stage s ON s.StageKey = p.CurrentStageKey
            WHERE fa.SourceSystemKey = @sourceSystemKey
            ORDER BY fa.SourceAccountId
            """, new { sourceSystemKey })).ToList();
    }

    private static async Task RemoveSourceAsync(int sourceSystemKey)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            DELETE FROM web.data_feed WHERE SourceSystemKey = @sourceSystemKey;
            DELETE r FROM dbo.account_reconciliation r JOIN dbo.fact_account fa ON fa.AccountKey = r.LegacyAccountKey WHERE fa.SourceSystemKey = @sourceSystemKey;
            DELETE p FROM dbo.fact_account_progress p JOIN dbo.fact_account fa ON fa.AccountKey = p.AccountKey WHERE fa.SourceSystemKey = @sourceSystemKey;
            DELETE FROM dbo.fact_account WHERE SourceSystemKey = @sourceSystemKey;
            DELETE FROM dbo.dim_source_system WHERE SourceSystemKey = @sourceSystemKey;
            """, new { sourceSystemKey });
    }

    private static async Task<RunResponse> RunAndWaitAsync(HttpClient client, int key)
    {
        var response = await client.PostAsJsonAsync("/api/admin/data-feeds/run", new { dataFeedKeys = new[] { key } });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        for (var i = 0; i < 100; i++)
        {
            var status = await client.GetFromJsonAsync<StatusResponse>("/api/admin/data-feeds/status");
            if (!status!.IsRunning) return (await client.GetFromJsonAsync<List<RunResponse>>($"/api/admin/data-feeds/{key}/runs"))![0];
            await Task.Delay(200);
        }
        Assert.Fail("The data feed run didn't finish within 20 seconds.");
        return null!;
    }

    [Fact]
    public async Task SourceSystems_ValidateAndListOnlyFeedSources()
    {
        var client = AdminClient();

        var bad = await client.PostAsJsonAsync("/api/admin/data-feeds/source-systems", new { sourceSystemCode = "has space", sourceSystemName = "" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var reserved = await client.PostAsJsonAsync("/api/admin/data-feeds/source-systems", new { sourceSystemCode = "privcloud", sourceSystemName = "Anything" });
        Assert.Equal(HttpStatusCode.Conflict, reserved.StatusCode);

        var source = await CreateSourceAsync(client);
        try
        {
            var options = (await client.GetFromJsonAsync<JsonObject>("/api/admin/data-feeds/options"))!;
            var codes = options["sourceSystems"]!.AsArray().Select(s => s!["sourceSystemCode"]!.GetValue<string>()).ToList();
            Assert.Contains(source.SourceSystemCode, codes);
            Assert.DoesNotContain("PRIVCLOUD", codes);
            Assert.DoesNotContain("DISCOVERY", codes);
            Assert.True(options["feedTypes"]!.AsArray().Single(t => t!["value"]!.GetValue<string>() == "AccountInventory")!["usesSourceSystem"]!.GetValue<bool>());
        }
        finally
        {
            await RemoveSourceAsync(source.SourceSystemKey);
        }
    }

    [Fact]
    public async Task Feed_RequiresItsOwnSourceSystem()
    {
        var client = AdminClient();
        var source = await CreateSourceAsync(client);
        var folder = Directory.CreateTempSubdirectory("BlueTrackAccountFeed_").FullName;
        try
        {
            var noSource = await client.PostAsJsonAsync("/api/admin/data-feeds", Feed($"CT_{Guid.NewGuid():N}", folder, null));
            Assert.Equal(HttpStatusCode.BadRequest, noSource.StatusCode);
            Assert.Contains("Source system is required", (await noSource.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!.GetValue<string>());

            var wrongType = await client.PostAsJsonAsync("/api/admin/data-feeds", Feed($"CT_{Guid.NewGuid():N}", folder, source.SourceSystemKey, "SafeAssignments"));
            Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);

            var first = await client.PostAsJsonAsync("/api/admin/data-feeds", Feed($"CT_{Guid.NewGuid():N}", folder, source.SourceSystemKey));
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            var second = await client.PostAsJsonAsync("/api/admin/data-feeds", Feed($"CT_{Guid.NewGuid():N}", folder, source.SourceSystemKey));
            Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
            Assert.Contains("already imports accounts for this source system", (await second.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!.GetValue<string>());
        }
        finally
        {
            await RemoveSourceAsync(source.SourceSystemKey);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task Runs_CreateUpdateDeleteAndRestoreAccounts_AndAnEmptyFileChangesNothing()
    {
        var client = AdminClient();
        var source = await CreateSourceAsync(client);
        var folder = Directory.CreateTempSubdirectory("BlueTrackAccountFeed_").FullName;
        var file = Path.Combine(folder, "accounts.csv");
        try
        {
            var created = await client.PostAsJsonAsync("/api/admin/data-feeds", Feed($"CT_{Guid.NewGuid():N}", folder, source.SourceSystemKey));
            var key = (await created.Content.ReadFromJsonAsync<KeyResponse>())!.DataFeedKey;

            // First run: two accounts, a row missing its Address, a duplicate, and an unknown platform (imported, reported).
            await File.WriteAllTextAsync(file,
                "UserName,Address,AccountName,PlatformName,OwnerName\r\n" +
                "Root,Host1.Example.com,,,Alice\r\n" +
                "svc_app,host2.example.com,App service,NoSuchPlatform_CT,\r\n" +
                "nobody,,,,\r\n" +
                "root,host1.example.com,,,\r\n");
            var run1 = await RunAndWaitAsync(client, key);
            Assert.Equal("CompletedWithErrors", run1.Outcome);
            Assert.Equal(3, run1.ErrorRows);
            Assert.StartsWith("2 created, 0 updated, 0 restored, 0 marked deleted", run1.Summary);

            var accounts = await AccountsAsync(source.SourceSystemKey);
            Assert.Equal(["root@host1.example.com", "svc_app@host2.example.com"], accounts.Select(a => a.SourceAccountId));
            var root = accounts[0];
            Assert.Equal("Root@Host1.Example.com", root.AccountName);   // default name keeps the file's case
            Assert.Equal("Discovered", root.StageName);
            Assert.Equal("Alice", root.OwnerName);

            // A feed account has no safe; D-212's auto-advance leaves it at Discovered (rolled back: other accounts stay as they are).
            await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
            {
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await connection.ExecuteAsync("EXEC dbo.usp_Load_AccountProgressAutoAdvance", transaction: transaction);
                var stage = await connection.QuerySingleAsync<string>("""
                    SELECT s.StageName FROM dbo.fact_account fa
                    JOIN dbo.fact_account_progress p ON p.AccountKey = fa.AccountKey
                    JOIN dbo.dim_blueprint_stage s ON s.StageKey = p.CurrentStageKey
                    WHERE fa.SourceSystemKey = @SourceSystemKey AND fa.SourceAccountId = 'root@host1.example.com'
                    """, new { source.SourceSystemKey }, transaction);
                Assert.Equal("Discovered", stage);
                await transaction.RollbackAsync();
            }

            // Second run: root is gone (marked deleted), svc_app renamed; owner in the file doesn't overwrite progress.
            await File.WriteAllTextAsync(file, "UserName,Address,AccountName\r\nsvc_app,host2.example.com,App service (renamed)\r\n");
            var run2 = await RunAndWaitAsync(client, key);
            Assert.Equal("Succeeded", run2.Outcome);
            Assert.StartsWith("0 created, 1 updated, 0 restored, 1 marked deleted", run2.Summary);
            accounts = await AccountsAsync(source.SourceSystemKey);
            Assert.True(accounts[0].IsDeleted && accounts[0].IsDeletedInSource);
            Assert.Equal("App service (renamed)", accounts[1].AccountName);

            // Third run: root is back, restored with its old progress row.
            await File.WriteAllTextAsync(file, "UserName,Address,AccountName\r\nroot,host1.example.com,\r\nsvc_app,host2.example.com,App service (renamed)\r\n");
            var run3 = await RunAndWaitAsync(client, key);
            Assert.StartsWith("0 created, 0 updated, 1 restored, 0 marked deleted, 1 unchanged, 0 linked", run3.Summary);
            accounts = await AccountsAsync(source.SourceSystemKey);
            Assert.False(accounts[0].IsDeleted);
            Assert.Equal("Alice", accounts[0].OwnerName);

            // A file with no valid rows fails the run and changes nothing.
            await File.WriteAllTextAsync(file, "UserName,Address\r\n");
            var run4 = await RunAndWaitAsync(client, key);
            Assert.Equal("Failed", run4.Outcome);
            Assert.Contains("no valid account rows", run4.ErrorMessage);
            Assert.All(await AccountsAsync(source.SourceSystemKey), a => Assert.False(a.IsDeleted));
        }
        finally
        {
            await RemoveSourceAsync(source.SourceSystemKey);
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>D-236: a pending-accounts export's SID user name and long address (over 100 characters together) are accepted; one over 400 is a row error.</summary>
    [Fact]
    public async Task Run_AcceptsALongIdentity_AndReportsOneOverTheLimit()
    {
        var client = AdminClient();
        var source = await CreateSourceAsync(client);
        var folder = Directory.CreateTempSubdirectory("BlueTrackAccountFeed_").FullName;
        var file = Path.Combine(folder, "accounts.csv");
        try
        {
            var created = await client.PostAsJsonAsync("/api/admin/data-feeds", Feed($"CT_{Guid.NewGuid():N}", folder, source.SourceSystemKey));
            var key = (await created.Content.ReadFromJsonAsync<KeyResponse>())!.DataFeedKey;

            var sid = "S-1-5-21-1004336348-1177238915-682003330-512345";
            var longAddress = "workstation-" + new string('a', 100) + ".corp.example.com";
            var tooLong = new string('b', 380) + "@" + new string('c', 30);   // 411 characters
            await File.WriteAllTextAsync(file,
                "UserName,Address\r\n" +
                $"{sid},{longAddress}\r\n" +
                $"{tooLong.Split('@')[0]},{tooLong.Split('@')[1]}\r\n");
            var run = await RunAndWaitAsync(client, key);

            Assert.Equal("CompletedWithErrors", run.Outcome);
            Assert.Equal(1, run.ErrorRows);
            Assert.StartsWith("1 created", run.Summary);
            var accounts = await AccountsAsync(source.SourceSystemKey);
            var account = Assert.Single(accounts);
            Assert.Equal($"{sid}@{longAddress}".ToLowerInvariant(), account.SourceAccountId);
            Assert.True(account.SourceAccountId.Length > 100);
        }
        finally
        {
            await RemoveSourceAsync(source.SourceSystemKey);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("*Domain Admins Group*", true)]
    [InlineData("*local GROUP users*", true)]
    [InlineData("  *Remote Desktop Users Group*  ", true)]
    [InlineData("*Administrators*", false)]       // no "group"
    [InlineData("Domain Group*", false)]          // no leading *
    [InlineData("*Domain Group", false)]          // no trailing *
    [InlineData("**", false)]
    [InlineData("S-1-5-21-1004336348-1177238915-682003330-512", false)]
    public void IsGroupUserName_NeedsAStarAtEachEndAndTheWordGroupInside(string userName, bool expected) =>
        Assert.Equal(expected, Api.Imports.AccountInventoryImportService.IsGroupUserName(userName));

    /// <summary>D-237: with SkipGroupRows on, "*...group*" rows are neither imported nor errors; an earlier import of one is marked deleted.</summary>
    [Fact]
    public async Task Run_SkipsGroupRows_WhenTheFeedSkipsThem_AndMarksEarlierOnesDeleted()
    {
        var client = AdminClient();
        var source = await CreateSourceAsync(client);
        var folder = Directory.CreateTempSubdirectory("BlueTrackAccountFeed_").FullName;
        var file = Path.Combine(folder, "accounts.csv");
        try
        {
            var created = await client.PostAsJsonAsync("/api/admin/data-feeds", Feed($"CT_{Guid.NewGuid():N}", folder, source.SourceSystemKey));
            var key = (await created.Content.ReadFromJsonAsync<KeyResponse>())!.DataFeedKey;

            // Off by default: the groups are imported like any row.
            await File.WriteAllTextAsync(file,
                "UserName,Address\r\n" +
                "S-1-5-21-1-2-3-1001,host1.example.com\r\n" +
                "*Domain Admins Group*,host1.example.com\r\n" +
                "*local GROUP users*,\r\n");
            var run1 = await RunAndWaitAsync(client, key);
            Assert.Equal(2, (await AccountsAsync(source.SourceSystemKey)).Count);   // the group row without an address is an error row
            Assert.Equal(1, run1.ErrorRows);
            Assert.DoesNotContain("group rows skipped", run1.Summary);

            // Turn it on: the same file now skips both group rows, reports no error, and marks the imported group deleted.
            var update = await client.PutAsJsonAsync($"/api/admin/data-feeds/{key}", Feed($"CT_{Guid.NewGuid():N}", folder, source.SourceSystemKey, skipGroupRows: true));
            Assert.Equal(HttpStatusCode.NoContent, update.StatusCode);
            var run2 = await RunAndWaitAsync(client, key);
            Assert.Equal("Succeeded", run2.Outcome);
            Assert.Equal(0, run2.ErrorRows);
            Assert.Contains("1 marked deleted", run2.Summary);
            Assert.EndsWith("2 group rows skipped", run2.Summary);
            var accounts = await AccountsAsync(source.SourceSystemKey);
            Assert.False(accounts.Single(a => a.UserName == "S-1-5-21-1-2-3-1001").IsDeleted);
            Assert.True(accounts.Single(a => a.UserName == "*Domain Admins Group*").IsDeleted);
        }
        finally
        {
            await RemoveSourceAsync(source.SourceSystemKey);
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public async Task OnlyAnAccountInventoryFeed_CanSkipGroupRows()
    {
        var client = AdminClient();
        var folder = Directory.CreateTempSubdirectory("BlueTrackAccountFeed_").FullName;
        try
        {
            var response = await client.PostAsJsonAsync("/api/admin/data-feeds",
                Feed($"CT_{Guid.NewGuid():N}", folder, null, feedType: "SafeAssignments", skipGroupRows: true));
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("skip group rows", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed record CyberArkAccount(long AccountKey, string AccountName, string UserName, string Address);
    private sealed record ListRow(long AccountKey, string? SourceSystemCode, long? LinkedToAccountKey, string? LinkedToAccountName);

    /// <summary>D-212 (phase 4b): an exact match links the feed account to the CyberArk account, moves it on, and hides it.</summary>
    [Fact]
    public async Task Run_LinksAnExactMatchToItsCyberArkAccount_AndHidesIt()
    {
        var client = AdminClient();
        // The test seed has no CyberArk account with an address, so the test adds one (tests run one at a time).
        var id = Guid.NewGuid().ToString("N")[..12];
        var cyberArk = await AddCyberArkAccountAsync($"ct_user_{id}", $"ct-host-{id}.example.com");
        var source = await CreateSourceAsync(client);
        var folder = Directory.CreateTempSubdirectory("BlueTrackAccountFeed_").FullName;
        try
        {
            var created = await client.PostAsJsonAsync("/api/admin/data-feeds", Feed($"CT_{Guid.NewGuid():N}", folder, source.SourceSystemKey));
            var key = (await created.Content.ReadFromJsonAsync<KeyResponse>())!.DataFeedKey;
            // The same account, in different case and with spaces; and one CyberArk doesn't have.
            await File.WriteAllTextAsync(Path.Combine(folder, "accounts.csv"),
                $"UserName,Address\r\n {cyberArk.UserName.ToUpperInvariant()} ,{cyberArk.Address.ToUpperInvariant()}\r\nnot_in_cyberark_ct,nowhere.example.com\r\n");

            var run = await RunAndWaitAsync(client, key);
            Assert.Contains("2 created", run.Summary);
            Assert.Contains("1 linked to CyberArk", run.Summary);

            var accounts = await AccountsAsync(source.SourceSystemKey);
            var linkedAccount = accounts.Single(a => a.SourceAccountId != "not_in_cyberark_ct@nowhere.example.com");
            Assert.Equal("Onboarded to Vault", linkedAccount.StageName);
            Assert.Equal("Discovered", accounts.Single(a => a.SourceAccountId == "not_in_cyberark_ct@nowhere.example.com").StageName);

            long feedAccountKey;
            await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
            {
                feedAccountKey = await connection.QuerySingleAsync<long>(
                    "SELECT AccountKey FROM dbo.fact_account WHERE SourceSystemKey = @SourceSystemKey AND SourceAccountId = @SourceAccountId",
                    new { source.SourceSystemKey, linkedAccount.SourceAccountId });
                var link = await connection.QuerySingleAsync<long>(
                    "SELECT LinkedAccountKey FROM dbo.vw_linked_feed_account WHERE AccountKey = @feedAccountKey", new { feedAccountKey });
                Assert.Equal(cyberArk.AccountKey, link);
                var stageChange = await connection.QuerySingleAsync<int>("""
                    SELECT COUNT(*) FROM web.audit_event e JOIN web.audit_field_change c ON c.AuditEventKey = e.AuditEventKey
                    WHERE e.EntityName = 'fact_account_progress' AND e.EntityKey = @entityKey AND c.FieldName = 'CurrentStageKey'
                      AND e.Detail LIKE 'Linked to CyberArk account%'
                    """, new { entityKey = feedAccountKey.ToString() });
                Assert.Equal(1, stageChange);
            }

            // Hidden from Account Progress by default; shown, with its link, on request.
            var search = Uri.EscapeDataString(cyberArk.UserName);
            var hidden = (await client.GetFromJsonAsync<List<ListRow>>($"/api/account-progress?search={search}&pageSize=200"))!;
            Assert.DoesNotContain(hidden, r => r.AccountKey == feedAccountKey);
            Assert.Contains(hidden, r => r.AccountKey == cyberArk.AccountKey);
            var shown = (await client.GetFromJsonAsync<List<ListRow>>($"/api/account-progress?search={search}&pageSize=200&linked=Show"))!;
            var row = Assert.Single(shown, r => r.AccountKey == feedAccountKey);
            Assert.Equal(cyberArk.AccountKey, row.LinkedToAccountKey);
            Assert.Equal(source.SourceSystemCode, row.SourceSystemCode);

            // A second run doesn't link it again.
            var again = await RunAndWaitAsync(client, key);
            Assert.Contains("0 linked to CyberArk", again.Summary);
        }
        finally
        {
            await RemoveSourceAsync(source.SourceSystemKey);
            Directory.Delete(folder, recursive: true);
            await RemoveCyberArkAccountsAsync(cyberArk.AccountKey);
        }
    }

    private sealed record MatchRow(long ReconciliationKey, string MatchMethod, long FeedAccountKey, long CyberArkAccountKey);

    private static async Task<CyberArkAccount> AddCyberArkAccountAsync(string userName, string address)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var account = await connection.QuerySingleAsync<CyberArkAccount>("""
            INSERT INTO dbo.fact_account (SourceSystemKey, SourceAccountId, AccountName, UserName, Address)
            OUTPUT inserted.AccountKey, inserted.AccountName, inserted.UserName, inserted.Address
            SELECT SourceSystemKey, @id, 'CT ' + @id, @userName, @address FROM dbo.dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD'
            """, new { id = Guid.NewGuid().ToString("N")[..20], userName, address });
        await connection.ExecuteAsync("""
            INSERT INTO dbo.fact_account_progress (AccountKey, CurrentStageKey, CurrentStatusKey)
            SELECT @AccountKey, (SELECT StageKey FROM dbo.dim_blueprint_stage WHERE StageName = 'Onboarded to Vault'),
                   (SELECT StatusKey FROM dbo.dim_progress_status WHERE StatusName = 'In Progress')
            """, new { account.AccountKey });
        return account;
    }

    private static async Task RemoveCyberArkAccountsAsync(params long[] accountKeys)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            DELETE FROM dbo.account_reconciliation WHERE CurrentAccountKey IN @accountKeys;
            DELETE FROM dbo.fact_account_progress WHERE AccountKey IN @accountKeys;
            DELETE FROM dbo.fact_account WHERE AccountKey IN @accountKeys;
            """, new { accountKeys });
    }

    /// <summary>D-212 (phase 4c): a loose match is proposed, not linked; Link and Dismiss settle it, and a dismissed pair stays dismissed.</summary>
    [Fact]
    public async Task PossibleMatches_AreProposed_ThenLinkedOrDismissed()
    {
        var client = AdminClient();
        var id = Guid.NewGuid().ToString("N")[..10];
        var first = await AddCyberArkAccountAsync($"ct_a_{id}", $"ct-a-{id}.corp.example.com");
        var second = await AddCyberArkAccountAsync($"ct_b_{id}", $"ct-b-{id}.corp.example.com");
        var source = await CreateSourceAsync(client);
        var folder = Directory.CreateTempSubdirectory("BlueTrackAccountFeed_").FullName;
        try
        {
            var created = await client.PostAsJsonAsync("/api/admin/data-feeds", Feed($"CT_{Guid.NewGuid():N}", folder, source.SourceSystemKey));
            var key = (await created.Content.ReadFromJsonAsync<KeyResponse>())!.DataFeedKey;
            // Short host names: the same accounts, but not an exact match.
            await File.WriteAllTextAsync(Path.Combine(folder, "accounts.csv"), $"UserName,Address\r\nct_a_{id},ct-a-{id}\r\nct_b_{id},ct-b-{id}\r\n");

            var run = await RunAndWaitAsync(client, key);
            Assert.Contains("0 linked to CyberArk, 2 possible matches", run.Summary);

            Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.Viewer").GetAsync("/api/reports/possible-matches")).StatusCode);
            var rows = (await client.GetFromJsonAsync<List<MatchRow>>("/api/reports/possible-matches?pageSize=200"))!;
            var linkRow = rows.Single(r => r.CyberArkAccountKey == first.AccountKey);
            var dismissRow = rows.Single(r => r.CyberArkAccountKey == second.AccountKey);
            Assert.Equal("FeedShortName", linkRow.MatchMethod);

            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/reports/possible-matches/{linkRow.ReconciliationKey}/link", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/reports/possible-matches/{dismissRow.ReconciliationKey}/dismiss", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/reports/possible-matches/{linkRow.ReconciliationKey}/link", null)).StatusCode);

            var accounts = await AccountsAsync(source.SourceSystemKey);
            Assert.Equal("Onboarded to Vault", accounts.Single(a => a.SourceAccountId == $"ct_a_{id}@ct-a-{id}").StageName);
            Assert.Equal("Discovered", accounts.Single(a => a.SourceAccountId == $"ct_b_{id}@ct-b-{id}").StageName);
            await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
            {
                Assert.Equal(first.AccountKey, await connection.QuerySingleAsync<long>(
                    "SELECT LinkedAccountKey FROM dbo.vw_linked_feed_account WHERE AccountKey = @FeedAccountKey", new { linkRow.FeedAccountKey }));
            }

            // Neither is open now; the next run proposes nothing again (the dismissed pair stays dismissed).
            rows = (await client.GetFromJsonAsync<List<MatchRow>>("/api/reports/possible-matches?pageSize=200"))!;
            Assert.DoesNotContain(rows, r => r.ReconciliationKey == linkRow.ReconciliationKey || r.ReconciliationKey == dismissRow.ReconciliationKey);
            var again = await RunAndWaitAsync(client, key);
            Assert.Contains("0 possible matches", again.Summary);
        }
        finally
        {
            await RemoveSourceAsync(source.SourceSystemKey);
            await RemoveCyberArkAccountsAsync(first.AccountKey, second.AccountKey);
            Directory.Delete(folder, recursive: true);
        }
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }
}
