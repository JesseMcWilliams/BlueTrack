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
            INSERT INTO web.cyberark_source_config
            SELECT * FROM OPENJSON(@json) WITH (
                CyberArkSourceConfigKey INT, ImportPrivilegeCloud BIT, ExportFolderPath NVARCHAR(400),
                PlatformsFileName NVARCHAR(200), UsersFileName NVARCHAR(200), GroupsFileName NVARCHAR(200),
                GroupMembersFileName NVARCHAR(200), SafesFileName NVARCHAR(200), AccountsFileName NVARCHAR(200),
                EntitlementsFileName NVARCHAR(200), ImportSelfHosted BIT, EvdDatabaseName NVARCHAR(128),
                ModifiedBy INT, ModifiedDate DATETIME2)
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
}
