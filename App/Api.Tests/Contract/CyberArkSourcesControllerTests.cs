using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-211 (Data Sources phase 3): the CyberArk sources' settings
/// (CyberArkSourcesController), gated by ManageDataSources. The one settings
/// row is put back the way the test found it.
/// </summary>
public class CyberArkSourcesControllerTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public CyberArkSourcesControllerTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private sealed record Check(string Item, bool Ok, string Message);

    private static object Settings(bool privilegeCloud, string? folder, bool selfHosted, string? evd, string entitlements = "Export Entitlements {yyyy-MM-dd}.csv") => new
    {
        importPrivilegeCloud = privilegeCloud,
        exportFolderPath = folder,
        platformsFileName = "Export_PlatformsList.csv",
        usersFileName = "Export_UsersList.csv",
        groupsFileName = "Export_GroupsList.csv",
        groupMembersFileName = "Export Local Group Members {yyyy-MM-dd}.csv",
        safesFileName = "Export_SafesList.csv",
        accountsFileName = "Export_AccountsList.csv",
        entitlementsFileName = entitlements,
        importSelfHosted = selfHosted,
        evdDatabaseName = evd
    };

    private static async Task<string?> SavedRowJsonAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleOrDefaultAsync<string>(
            "SELECT * FROM web.cyberark_source_config WHERE CyberArkSourceConfigKey = 1 FOR JSON PATH, WITHOUT_ARRAY_WRAPPER");
    }

    private static async Task RestoreAsync(string? savedRowJson)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("DELETE FROM web.cyberark_source_config");
        if (savedRowJson is null) return;
        await connection.ExecuteAsync("""
            INSERT INTO web.cyberark_source_config (CyberArkSourceConfigKey, ImportPrivilegeCloud, ExportFolderPath, PlatformsFileName, UsersFileName, GroupsFileName, GroupMembersFileName, SafesFileName, AccountsFileName, EntitlementsFileName, ImportSelfHosted, EvdDatabaseName, ModifiedBy, ModifiedDate, SelfHostedSource, PvwaBaseUrl, PvwaCredentialKey, PvwaLogonMethod, DirectPullTime, DirectMaxChangePercent)
            SELECT CyberArkSourceConfigKey, ImportPrivilegeCloud, ExportFolderPath, PlatformsFileName, UsersFileName, GroupsFileName, GroupMembersFileName, SafesFileName, AccountsFileName, EntitlementsFileName, ImportSelfHosted, EvdDatabaseName, ModifiedBy, ModifiedDate, SelfHostedSource, PvwaBaseUrl, PvwaCredentialKey, PvwaLogonMethod, DirectPullTime, DirectMaxChangePercent FROM OPENJSON(@json) WITH (
                CyberArkSourceConfigKey INT, ImportPrivilegeCloud BIT, ExportFolderPath NVARCHAR(400),
                PlatformsFileName NVARCHAR(200), UsersFileName NVARCHAR(200), GroupsFileName NVARCHAR(200),
                GroupMembersFileName NVARCHAR(200), SafesFileName NVARCHAR(200), AccountsFileName NVARCHAR(200),
                EntitlementsFileName NVARCHAR(200), ImportSelfHosted BIT, EvdDatabaseName NVARCHAR(128),
                ModifiedBy INT, ModifiedDate DATETIME2, SelfHostedSource NVARCHAR(10), PvwaBaseUrl NVARCHAR(400), PvwaCredentialKey INT, PvwaLogonMethod NVARCHAR(20), DirectPullTime NVARCHAR(5), DirectMaxChangePercent INT)
            """, new { json = savedRowJson });
    }

    [Fact]
    public async Task Endpoints_AsViewer_AreForbidden()
    {
        var client = Client("TestUser.Viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/cyberark-sources")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PutAsJsonAsync("/api/admin/cyberark-sources", Settings(false, null, true, "BlueTrackTest"))).StatusCode);
    }

    [Fact]
    public async Task Save_RejectsInvalidValues()
    {
        var client = Client("TestUser.Admin");

        var noSource = await client.PutAsJsonAsync("/api/admin/cyberark-sources", Settings(false, null, false, null));
        Assert.Equal(HttpStatusCode.BadRequest, noSource.StatusCode);
        Assert.Contains("at least one source", (await noSource.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!.GetValue<string>());

        var bad = await client.PutAsJsonAsync("/api/admin/cyberark-sources", Settings(true, "relative\\path", true, "Bad-Name", entitlements: "Export *.csv"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var detail = (await bad.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!.GetValue<string>();
        Assert.Contains("full path", detail);
        Assert.Contains("Entitlements file: * and ?", detail);
        Assert.Contains("letters, digits and underscores", detail);
    }

    [Fact]
    public async Task SaveGetAndTest_RoundTrip_AndAreAuditLogged()
    {
        var client = Client("TestUser.Admin");
        var saved = await SavedRowJsonAsync();
        var folder = Directory.CreateTempSubdirectory("BlueTrackCyberArkContract_").FullName;
        try
        {
            // Both sources; the EVD "database" is BlueTrackTest itself, which exists.
            var put = await client.PutAsJsonAsync("/api/admin/cyberark-sources", Settings(true, folder + "\\", true, "BlueTrackTest"));
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

            var got = (await client.GetFromJsonAsync<JsonObject>("/api/admin/cyberark-sources"))!;
            Assert.True(got["isConfigured"]!.GetValue<bool>());
            Assert.Equal(folder, got["settings"]!["exportFolderPath"]!.GetValue<string>());   // trailing backslash trimmed
            Assert.Equal("BlueTrackTest", got["settings"]!["evdDatabaseName"]!.GetValue<string>());

            await using (var connection = new SqlConnection(TestDatabase.ConnectionString))
            {
                var changed = await connection.QuerySingleAsync<int>("""
                    SELECT COUNT(*) FROM web.audit_event e
                    JOIN web.audit_field_change c ON c.AuditEventKey = e.AuditEventKey
                    WHERE e.EntityName = 'cyberark_source_config' AND c.FieldName = 'ExportFolderPath' AND c.NewValue = @folder
                    """, new { folder });
                Assert.Equal(1, changed);
            }

            // Test: the EVD database exists; the folder is readable; only the platforms file is there.
            File.WriteAllText(Path.Combine(folder, "Export_PlatformsList.csv"), "PlatformID\n");
            var checks = (await (await client.PostAsJsonAsync("/api/admin/cyberark-sources/test", Settings(true, folder, true, "BlueTrackTest")))
                .Content.ReadFromJsonAsync<List<Check>>())!;
            Assert.True(checks.Single(c => c.Item == "EVD database").Ok);
            Assert.True(checks.Single(c => c.Item == "Export folder").Ok);
            Assert.True(checks.Single(c => c.Item == "Platforms").Ok);
            var entitlements = checks.Single(c => c.Item == "Entitlements");
            Assert.False(entitlements.Ok);
            Assert.Contains($"Export Entitlements {DateTime.Today:yyyy-MM-dd}.csv", entitlements.Message);

            var missingDb = (await (await client.PostAsJsonAsync("/api/admin/cyberark-sources/test", Settings(false, null, true, $"NoSuchDb_{Guid.NewGuid():N}")))
                .Content.ReadFromJsonAsync<List<Check>>())!;
            Assert.False(Assert.Single(missingDb).Ok);
        }
        finally
        {
            await RestoreAsync(saved);
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>
    /// D-228: Test reads each file's header row and reports the required
    /// columns it lacks (from web.cyberark_import_column, as the import does).
    /// </summary>
    [Fact]
    public async Task Test_ChecksEachFilesHeader_AgainstTheImportsColumnList()
    {
        var client = Client("TestUser.Admin");
        var folder = Directory.CreateTempSubdirectory("BlueTrackCyberArkHeaders_").FullName;
        try
        {
            // The new Users layout: no Email (optional), extra columns; with a UTF-8 byte order mark.
            File.WriteAllText(Path.Combine(folder, "Export_UsersList.csv"),
                "\"UserID\",\"Username\",\"UserType\",\"Source\",\"ComponentUser\",\"FirstName\",\"LastName\",\"Enabled\",\"Suspended\",\"Location\"\r\n",
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            // Groups without GroupName (required).
            File.WriteAllText(Path.Combine(folder, "Export_GroupsList.csv"), "\"GroupID\",\"Description\",\"Directory\"\r\n\"1\",\"x\",\"y\"\r\n");
            // Accounts, empty: every required column is missing.
            File.WriteAllText(Path.Combine(folder, "Export_AccountsList.csv"), "");

            var checks = (await (await client.PostAsJsonAsync("/api/admin/cyberark-sources/test", Settings(true, folder, false, null)))
                .Content.ReadFromJsonAsync<List<Check>>())!;

            var users = checks.Single(c => c.Item == "Users");
            Assert.True(users.Ok);
            Assert.Equal("Export_UsersList.csv found, with the required columns. Not in the file, so imported as empty: Email.", users.Message);

            var groups = checks.Single(c => c.Item == "Groups");
            Assert.False(groups.Ok);
            Assert.Equal("Export_GroupsList.csv is missing the required column(s): GroupName. The import would fail.", groups.Message);

            var accounts = checks.Single(c => c.Item == "Accounts");
            Assert.False(accounts.Ok);
            Assert.Contains("missing the required column(s): AccountID, AccountName, Address, UserName, PlatformID, SafeName, Deleted.", accounts.Message);

            Assert.False(checks.Single(c => c.Item == "Safes").Ok);   // not there at all
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }


    // ---- D-248: Self-Hosted Direct ----

    private static object DirectSettings(string? url, int? credentialKey, string method = "CyberArk", string pullTime = "01:30", int percent = 20) => new
    {
        importPrivilegeCloud = false,
        platformsFileName = "Export_PlatformsList.csv",
        usersFileName = "Export_UsersList.csv",
        groupsFileName = "Export_GroupsList.csv",
        groupMembersFileName = "Export Local Group Members {yyyy-MM-dd}.csv",
        safesFileName = "Export_SafesList.csv",
        accountsFileName = "Export_AccountsList.csv",
        entitlementsFileName = "Export Entitlements {yyyy-MM-dd}.csv",
        importSelfHosted = true,
        selfHostedSource = "Direct",
        pvwaBaseUrl = url,
        pvwaCredentialKey = credentialKey,
        pvwaLogonMethod = method,
        directPullTime = pullTime,
        directMaxChangePercent = percent
    };

    private static async Task<int> AddCredentialAsync(string name)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<int>("""
            INSERT INTO web.credential (CredentialName, BackendType, VaultSafe, VaultFolder, VaultObject)
            VALUES (@name, 'CyberArkCP', 'S', 'Root', 'O');
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """, new { name });
    }

    [Fact]
    public async Task Get_OffersTheFourSources_WithPrivilegeCloudDirectPlanned()
    {
        var got = (await Client("TestUser.Admin").GetFromJsonAsync<JsonObject>("/api/admin/cyberark-sources"))!;

        var options = got["options"]!.AsArray();
        Assert.Equal(["SelfHostedEvd", "SelfHostedDirect", "PrivilegeCloudFolder", "PrivilegeCloudDirect"], options.Select(o => o!["key"]!.GetValue<string>()));
        Assert.False(options[3]!["available"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Save_DirectSource_RoundTrips_AndAuditsTheNewFields()
    {
        var client = Client("TestUser.Admin");
        var saved = await SavedRowJsonAsync();
        var credentialName = "DirectTest" + Guid.NewGuid().ToString("N")[..8];
        var credentialKey = await AddCredentialAsync(credentialName);
        var url = $"https://pvwa-{Guid.NewGuid():N}.example.com";
        try
        {
            var put = await client.PutAsJsonAsync("/api/admin/cyberark-sources", DirectSettings(url + "/", credentialKey, "LDAP", "01:15", 35));
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);

            var settings = (await client.GetFromJsonAsync<JsonObject>("/api/admin/cyberark-sources"))!["settings"]!;
            Assert.Equal("Direct", settings["selfHostedSource"]!.GetValue<string>());
            Assert.Equal(url, settings["pvwaBaseUrl"]!.GetValue<string>());   // trailing slash trimmed
            Assert.Equal(credentialKey, settings["pvwaCredentialKey"]!.GetValue<int>());
            Assert.Equal("LDAP", settings["pvwaLogonMethod"]!.GetValue<string>());
            Assert.Equal("01:15", settings["directPullTime"]!.GetValue<string>());
            Assert.Equal(35, settings["directMaxChangePercent"]!.GetValue<int>());
            Assert.Null(settings["evdDatabaseName"]);

            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            var fields = (await connection.QueryAsync<string>("""
                SELECT c.FieldName FROM web.audit_event e
                JOIN web.audit_field_change c ON c.AuditEventKey = e.AuditEventKey
                WHERE e.EntityName = 'cyberark_source_config' AND c.FieldName IN ('SelfHostedSource', 'PvwaBaseUrl', 'DirectMaxChangePercent') AND c.NewValue IN ('Direct', @url, '35')
                """, new { url })).ToList();
            Assert.Contains("SelfHostedSource", fields);
            Assert.Contains("PvwaBaseUrl", fields);
            Assert.Contains("DirectMaxChangePercent", fields);
        }
        finally
        {
            await RestoreAsync(saved);
            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            await connection.ExecuteAsync("DELETE FROM web.credential WHERE CredentialKey = @credentialKey", new { credentialKey });
        }
    }

    [Fact]
    public async Task Save_DirectSource_RejectsABadAddress_AMissingCredential_AndAnUnknownCredential()
    {
        var client = Client("TestUser.Admin");

        var http = await client.PutAsJsonAsync("/api/admin/cyberark-sources", DirectSettings("http://pvwa.example.com", 1));
        Assert.Equal(HttpStatusCode.BadRequest, http.StatusCode);
        Assert.Contains("PVWA address must be https://host", (await http.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!.GetValue<string>());

        var none = await client.PutAsJsonAsync("/api/admin/cyberark-sources", DirectSettings("https://pvwa.example.com", null));
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Contains("A credential is required", (await none.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!.GetValue<string>());

        var unknown = await client.PutAsJsonAsync("/api/admin/cyberark-sources", DirectSettings("https://pvwa.example.com", 2_000_000_000));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.Contains("credential doesn't exist", (await unknown.Content.ReadFromJsonAsync<JsonObject>())!["detail"]!.GetValue<string>());
    }

    [Fact]
    public async Task DirectPulls_ReturnsTheLog_NewestFirst_AndWhetherAPullIsRunning()
    {
        var client = Client("TestUser.Admin");
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var maxBefore = await connection.QuerySingleAsync<long>("SELECT ISNULL(MAX(PullKey), 0) FROM dbo.cyberark_direct_pull");
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO dbo.cyberark_direct_pull (Status, CompletedAt, TriggeredBy, SafeCount, AccountCount, Message)
                VALUES ('Rejected', SYSUTCDATETIME(), 'schedule', 10, 20, 'Safes went from 40 to 10')
                """);

            var body = (await client.GetFromJsonAsync<JsonObject>("/api/admin/cyberark-sources/direct/pulls"))!;

            Assert.False(body["running"]!.GetValue<bool>());
            var newest = body["pulls"]!.AsArray()[0]!;
            Assert.Equal(("Rejected", "schedule", 10, 20, "Safes went from 40 to 10"),
                (newest["status"]!.GetValue<string>(), newest["triggeredBy"]!.GetValue<string>(), newest["safeCount"]!.GetValue<int>(),
                 newest["accountCount"]!.GetValue<int>(), newest["message"]!.GetValue<string>()));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM dbo.cyberark_direct_pull WHERE PullKey > @maxBefore", new { maxBefore });
        }
    }

    [Fact]
    public async Task DirectPull_WhenDirectIsNotTheSavedSource_IsRefused_AndNothingStarts()
    {
        var client = Client("TestUser.Admin");
        var saved = await SavedRowJsonAsync();
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/admin/cyberark-sources", Settings(false, null, true, "BlueTrackTest"))).StatusCode);

            var response = await client.PostAsJsonAsync("/api/admin/cyberark-sources/direct/pull", new { acceptChange = false });

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("Save Self-Hosted Direct", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await RestoreAsync(saved);
        }
    }

    [Fact]
    public async Task DirectEndpoints_AsViewer_AreForbidden()
    {
        var client = Client("TestUser.Viewer");

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/cyberark-sources/direct/pulls")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync("/api/admin/cyberark-sources/direct/pull", new { acceptChange = true })).StatusCode);
    }

    [Fact]
    public async Task Credentials_ListsNamesOnly_ForAnAdmin_AndIsForbiddenToAViewer()
    {
        var name = "DirectList" + Guid.NewGuid().ToString("N")[..8];
        var key = await AddCredentialAsync(name);
        try
        {
            var body = (await Client("TestUser.Admin").GetFromJsonAsync<JsonArray>("/api/admin/cyberark-sources/credentials"))!;

            var row = body.Single(c => c!["credentialName"]!.GetValue<string>() == name)!.AsObject();
            Assert.Equal(key, row["credentialKey"]!.GetValue<int>());
            Assert.Equal("CyberArkCP", row["backendType"]!.GetValue<string>());
            Assert.Equal(["credentialKey", "credentialName", "backendType", "backendReady", "backendNote"], row.Select(p => p.Key));   // nothing about the Safe, object or password

            Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.Viewer").GetAsync("/api/admin/cyberark-sources/credentials")).StatusCode);
        }
        finally
        {
            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            await connection.ExecuteAsync("DELETE FROM web.credential WHERE CredentialKey = @key", new { key });
        }
    }

    // ---- D-250 ----

    [Fact]
    public async Task Credentials_SayWhetherTheirBackendIsSetUp()
    {
        var name = "ReadyTest" + Guid.NewGuid().ToString("N")[..8];
        var key = await AddCredentialAsync(name);
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var saved = await connection.QuerySingleOrDefaultAsync<string?>("SELECT BackendSettings FROM web.secrets_store WHERE BackendType = 'CyberArkCP'");
        var client = Client("TestUser.Admin");
        async Task<JsonObject> Mine() =>
            (await client.GetFromJsonAsync<JsonArray>("/api/admin/cyberark-sources/credentials"))!.Single(c => c!["credentialKey"]!.GetValue<int>() == key)!.AsObject();
        try
        {
            await connection.ExecuteAsync("UPDATE web.secrets_store SET BackendSettings = NULL WHERE BackendType = 'CyberArkCP'");
            var notReady = await Mine();
            Assert.False(notReady["backendReady"]!.GetValue<bool>());
            Assert.Contains("no AppID", notReady["backendNote"]!.GetValue<string>());

            await connection.ExecuteAsync("UPDATE web.secrets_store SET BackendSettings = N'{\"AppId\":\"APP_Test\"}' WHERE BackendType = 'CyberArkCP'");
            var ready = await Mine();
            Assert.True(ready["backendReady"]!.GetValue<bool>());
            Assert.Null(ready["backendNote"]);
        }
        finally
        {
            await connection.ExecuteAsync("UPDATE web.secrets_store SET BackendSettings = @saved WHERE BackendType = 'CyberArkCP'; DELETE FROM web.credential WHERE CredentialKey = @key", new { saved, key });
        }
    }

    [Fact]
    public async Task DirectStatus_IsOffUnlessDirectIsSaved_ThenShowsTheLatestPull()
    {
        var client = Client("TestUser.Admin");
        var saved = await SavedRowJsonAsync();
        var credentialKey = await AddCredentialAsync("StatusTest" + Guid.NewGuid().ToString("N")[..8]);
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var maxBefore = await connection.QuerySingleAsync<long>("SELECT ISNULL(MAX(PullKey), 0) FROM dbo.cyberark_direct_pull");
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/admin/cyberark-sources", Settings(false, null, true, "BlueTrackTest"))).StatusCode);
            var off = (await client.GetFromJsonAsync<JsonObject>("/api/admin/cyberark-sources/direct/status"))!;
            Assert.False(off["enabled"]!.GetValue<bool>());

            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/admin/cyberark-sources", DirectSettings("https://pvwa.example.com", credentialKey))).StatusCode);
            await connection.ExecuteAsync("INSERT INTO dbo.cyberark_direct_pull (Status, CompletedAt, TriggeredBy, Message) VALUES ('Rejected', SYSUTCDATETIME(), 'schedule', 'Safes went from 40 to 10')");
            var on = (await client.GetFromJsonAsync<JsonObject>("/api/admin/cyberark-sources/direct/status"))!;
            Assert.True(on["enabled"]!.GetValue<bool>());
            Assert.Equal("Rejected", on["latestStatus"]!.GetValue<string>());
            Assert.Equal("Safes went from 40 to 10", on["latestMessage"]!.GetValue<string>());

            Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.Viewer").GetAsync("/api/admin/cyberark-sources/direct/status")).StatusCode);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM dbo.cyberark_direct_pull WHERE PullKey > @maxBefore", new { maxBefore });
            await RestoreAsync(saved);
            await connection.ExecuteAsync("DELETE FROM web.credential WHERE CredentialKey = @credentialKey", new { credentialKey });
        }
    }

    [Fact]
    public async Task AdminCredentialsList_ShowsWhatUsesACredential()
    {
        var client = Client("TestUser.Admin");
        var saved = await SavedRowJsonAsync();
        var credentialKey = await AddCredentialAsync("UsedByTest" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync("/api/admin/cyberark-sources", DirectSettings("https://pvwa.example.com", credentialKey))).StatusCode);

            var all = (await client.GetFromJsonAsync<JsonArray>("/api/admin/credentials"))!;

            var usedBy = all.Single(c => c!["credentialKey"]!.GetValue<int>() == credentialKey)!["usedBy"]!.AsArray().Select(u => u!.GetValue<string>());
            Assert.Equal(["CyberArk sources (Self-Hosted Direct)"], usedBy);
        }
        finally
        {
            await RestoreAsync(saved);
            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            await connection.ExecuteAsync("DELETE FROM web.credential WHERE CredentialKey = @credentialKey", new { credentialKey });
        }
    }
}
