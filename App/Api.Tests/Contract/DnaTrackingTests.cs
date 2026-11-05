using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using BlueTrack.Api.Dna;
using BlueTrack.Api.Tests.Integration;
using ClosedXML.Excel;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-341 to D-344: DNA's newer report format, the DNA data feed, sorting, tracking DNA accounts in BlueTrack (by hand and
/// by tracking rules), purge and retention, and the columns that need ViewDnaSensitiveColumns. The workbook is made up, in
/// the newer report's shape: summary rows above each scan sheet's header, and a hard-coded credentials sheet never read.
/// </summary>
[Collection("DnaImport")]   // each import replaces the stored DNA accounts, so these run one at a time
public class DnaTrackingTests(BlueTrackWebApplicationFactory factory) : IClassFixture<BlueTrackWebApplicationFactory>, IAsyncLifetime
{
    private const string SecretValue = "ThisIsMy_FAKE_Password6!";
    private readonly string _token = $"dt{Guid.NewGuid():N}"[..10];

    private HttpClient Client(string user)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            DELETE FROM web.dna_account; DELETE FROM web.dna_hard_coded_credential; DELETE FROM web.dna_import;
            DELETE FROM web.dna_track_rule WHERE RuleName LIKE @token + '%';
            DELETE FROM web.dna_column_setting; UPDATE web.app_config SET DnaRetentionDays = NULL;
            DELETE p FROM dbo.fact_account_progress p JOIN dbo.fact_account fa ON fa.AccountKey = p.AccountKey
                JOIN dbo.dim_source_system s ON s.SourceSystemKey = fa.SourceSystemKey WHERE s.SourceSystemCode = 'DNA' AND fa.UserName LIKE @token + '%';
            DELETE fa FROM dbo.fact_account fa JOIN dbo.dim_source_system s ON s.SourceSystemKey = fa.SourceSystemKey
                WHERE s.SourceSystemCode = 'DNA' AND fa.UserName LIKE @token + '%';
            """, new { token = _token });
    }

    /// <summary>The newer report: a Windows scan (a local and a domain account; the domain one also on a second machine), a domain scan, a database scan.</summary>
    private byte[] Workbook()
    {
        using var workbook = new XLWorkbook();
        workbook.AddWorksheet("Executive Summary").Cell(1, 1).Value = "CyberArk DNA | Executive Summary";

        var windows = workbook.AddWorksheet("Windows Scan");
        windows.Cell(1, 1).Value = "CyberArk DNA | Discovery and Audit";
        windows.Cell(2, 1).Value = "SCAN SUMMARY";
        windows.Cell(3, 1).Value = "Total machines identified:";
        string[] winHeader = ["Machine Name", "Machine Type", "Account Name", "Account Display Name", "Account Type", "Account Category", "Account Group",
            "Threat Cause", "Account Description", "Compliance Status", "Account State", "Password Never Expires", "Password Age", "Password Last Set", "Last Login Date", "OS Version", "Details"];
        for (var c = 0; c < winHeader.Length; c++) windows.Cell(11, c + 1).Value = winHeader[c];
        void Win(int row, string machine, string account, string type, string category, double lastLogin)
        {
            windows.Cell(row, 1).Value = machine; windows.Cell(row, 2).Value = "Server"; windows.Cell(row, 3).Value = account; windows.Cell(row, 5).Value = type;
            windows.Cell(row, 6).Value = category; windows.Cell(row, 7).Value = "Administrators"; windows.Cell(row, 9).Value = "Built-in account";
            windows.Cell(row, 10).Value = "Non-Compliant"; windows.Cell(row, 11).Value = "Enabled"; windows.Cell(row, 12).Value = "Yes"; windows.Cell(row, 13).Value = 27;
            windows.Cell(row, 14).Value = 46304.2083912037; windows.Cell(row, 15).Value = lastLogin; windows.Cell(row, 16).Value = "Windows Server 2022"; windows.Cell(row, 17).Value = "N/A";
        }
        Win(12, "SRV01.corp.example", $"{_token}_local", "Local", "Privileged Shared", 46328.74);
        Win(13, "SRV01.corp.example", $"{_token}_dom", "Domain: corp.example", "Privileged Personal", 46330.79);
        Win(14, "SRV02.corp.example", $"{_token}_dom", "Domain: corp.example", "Privileged Personal", 46330.79);

        var domain = workbook.AddWorksheet("Domain Scan");
        domain.Cell(1, 1).Value = "CyberArk DNA | Discovery and Audit";
        string[] domHeader = ["Machine Name", "Account Name", "Account Type", "Account Category", "Service Account Type", "SPN Description", "Compliance Status"];
        for (var c = 0; c < domHeader.Length; c++) domain.Cell(9, c + 1).Value = domHeader[c];
        domain.Cell(10, 1).Value = "APP01.corp.example"; domain.Cell(10, 2).Value = $"{_token}_svc"; domain.Cell(10, 3).Value = "Domain: corp.example";
        domain.Cell(10, 4).Value = "Service Account"; domain.Cell(10, 5).Value = "Service Principle Name (SPN)"; domain.Cell(10, 6).Value = "HTTP/APP01"; domain.Cell(10, 7).Value = "Non-Compliant";

        var database = workbook.AddWorksheet("Database Scan");
        database.Cell(1, 1).Value = "CyberArk DNA | Discovery and Audit";
        string[] dbHeader = ["Machine Name", "Instance Name", "Account Name", "Account Type", "Account Category", "Roles", "Permissions", "Account State"];
        for (var c = 0; c < dbHeader.Length; c++) database.Cell(2, c + 1).Value = dbHeader[c];
        database.Cell(3, 1).Value = "DB01.corp.example"; database.Cell(3, 2).Value = "MSSQLSERVER"; database.Cell(3, 3).Value = $"{_token}_sa";
        database.Cell(3, 4).Value = "Database Sql Login"; database.Cell(3, 5).Value = "Privileged"; database.Cell(3, 6).Value = "sysadmin"; database.Cell(3, 7).Value = "CONNECT SQL"; database.Cell(3, 8).Value = "Enabled";

        var hcc = workbook.AddWorksheet("Hard-Coded Credentials");
        hcc.Cell(1, 1).Value = "CyberArk DNA | Discovery and Audit";
        string[] hccHeader = ["Machine Name", "Account Name", "Hard-Coded in File", "Hard-Coded Credential"];
        for (var c = 0; c < hccHeader.Length; c++) hcc.Cell(2, c + 1).Value = hccHeader[c];
        string[] hccExtra = ["Password Length", "Target System Address"];
        for (var c = 0; c < hccExtra.Length; c++) hcc.Cell(2, hccHeader.Length + c + 1).Value = hccExtra[c];
        // As DNA writes it: the file's line, the password masked; and one it left readable, which the import masks.
        hcc.Cell(3, 1).Value = "APP02"; hcc.Cell(3, 2).Value = $"{_token}_hcc"; hcc.Cell(3, 3).Value = @"C:\app\web.config";
        hcc.Cell(3, 4).Value = "Line: 12 - \"Server=DB01;User Id=app;Password=*****;\""; hcc.Cell(3, 5).Value = 5; hcc.Cell(3, 6).Value = "DB01.corp.example";
        hcc.Cell(4, 1).Value = "APP03"; hcc.Cell(4, 2).Value = $"{_token}_hcc2"; hcc.Cell(4, 3).Value = @"D:\svc\app.ini";
        hcc.Cell(4, 4).Value = $"Line: 3 - \"pwd={SecretValue};uid=svc\"";

        workbook.AddWorksheet("What is Pass-the-Hash").Cell(1, 1).Value = "Pass-the-Hash - Where is it hiding?";
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static MultipartFormDataContent Upload(byte[] bytes)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        content.Add(file, "file", "DNA Report.xlsx");
        return content;
    }

    private async Task<JsonObject> ImportAsync()
    {
        var response = await Client("TestUser.Admin").PostAsync("/api/admin/dna/import", Upload(Workbook()));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private static async Task<Dictionary<string, int>> KeysAsync(SqlConnection connection) =>
        (await connection.QueryAsync<(string Name, string? Machine, int Key)>("SELECT AccountName, MachineName, DnaAccountKey FROM web.dna_account"))
            .ToDictionary(r => $"{r.Name}@{r.Machine}", r => r.Key);

    [Fact]
    public async Task TheNewerFormat_IsRead_WithItsNewColumns_AndNoHardCodedCredentials()
    {
        var latest = (await ImportAsync())["latest"]!;
        Assert.Equal("Windows Scan=3; Domain Scan=1; Database Scan=1", latest["sheetCounts"]!.GetValue<string>());

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var local = await connection.QuerySingleAsync<(bool IsDomain, string MachineType, string OsVersion, string Description, DateTime LastLogin)>(
            "SELECT IsDomainAccount, MachineType, OsVersion, AccountDescription, LastLoginDate FROM web.dna_account WHERE AccountName = @n", new { n = $"{_token}_local" });
        Assert.Equal((false, "Server", "Windows Server 2022", "Built-in account"), (local.IsDomain, local.MachineType, local.OsVersion, local.Description));
        Assert.Equal(new DateTime(2026, 11, 2), local.LastLogin.Date);
        Assert.True(await connection.QuerySingleAsync<bool>("SELECT TOP 1 IsDomainAccount FROM web.dna_account WHERE AccountName = @n", new { n = $"{_token}_dom" }));
        Assert.Equal(("sysadmin", "CONNECT SQL"), await connection.QuerySingleAsync<(string, string)>("SELECT Roles, DbPermissions FROM web.dna_account WHERE AccountName = @n", new { n = $"{_token}_sa" }));
        Assert.Equal(0, await connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM web.dna_account WHERE AccountName = @n OR Details LIKE '%' + @secret + '%' OR AccountDescription LIKE '%' + @secret + '%'",
            new { n = $"{_token}_hcc", secret = SecretValue }));

        // D-345: the hard-coded credentials go into their own table, DNA's masked line kept, a readable password masked.
        Assert.Equal(2, latest["hardCodedCount"]!.GetValue<int>());
        var hcc = (await connection.QueryAsync<(string Account, string File, string Location, int? Length, string? Target)>(
            "SELECT AccountName, HardCodedInFile, CredentialLocation, PasswordLength, TargetSystemAddress FROM web.dna_hard_coded_credential ORDER BY AccountName")).ToList();
        Assert.Equal(($"{_token}_hcc", @"C:\app\web.config", "Line: 12 - \"Server=DB01;User Id=app;Password=*****;\"", (int?)5, (string?)"DB01.corp.example"), hcc[0]);
        Assert.Equal($"Line: 3 - \"pwd={new string('*', SecretValue.Length)};uid=svc\"", hcc[1].Location);
        Assert.Equal(0, await connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM web.dna_hard_coded_credential WHERE CONCAT(CredentialLocation, HardCodedInFile, Details) LIKE '%' + @secret + '%'", new { secret = SecretValue }));
    }

    // D-345: only holders of ViewDnaSensitiveColumns open the report and its export.
    [Fact]
    public async Task TheHardCodedCredentialsReport_IsForViewDnaSensitiveColumnsOnly_AndExports()
    {
        await ImportAsync();
        var admin = Client("TestUser.Admin");
        var page = await admin.GetAsync("/api/reports/dna-hard-coded-credentials?sort=AccountName:desc&pageSize=100");
        page.EnsureSuccessStatusCode();
        Assert.Equal("2", page.Headers.GetValues("X-Total-Count").Single());
        Assert.Equal([$"{_token}_hcc2", $"{_token}_hcc"], (await page.Content.ReadFromJsonAsync<JsonArray>())!.Select(r => r!["accountName"]!.GetValue<string>()));
        var searched = (await admin.GetFromJsonAsync<JsonArray>("/api/reports/dna-hard-coded-credentials?search=app.ini"))!;
        Assert.Equal($"{_token}_hcc2", searched.Single()!["accountName"]!.GetValue<string>());

        var csv = await admin.GetStringAsync("/api/reports/dna-hard-coded-credentials/export?format=csv");
        Assert.StartsWith("Machine,Machine type,OS version,Application server,Application,Site,Account,File,Where in the file (masked),Password length", csv.TrimStart('\uFEFF'));
        Assert.DoesNotContain(SecretValue, csv);

        // ViewDiscoveredAccounts (DNA Accounts) isn't enough.
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.Viewer").GetAsync("/api/reports/dna-hard-coded-credentials")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.Analyst").GetAsync("/api/reports/dna-hard-coded-credentials/export?format=csv")).StatusCode);

        // Purge takes them too.
        (await admin.PostAsync("/api/admin/dna/purge", null)).EnsureSuccessStatusCode();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        Assert.Equal(0, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.dna_hard_coded_credential"));
    }

    /// <summary>
    /// D-346 (the user's rules, 2026-11-05): by Account Type. A local account links on its user name and machine (full or
    /// short name); a domain account whose domain is given, on its user name and that domain (or its first part); a domain
    /// given as a SID, a group or an account with no domain doesn't link.
    /// </summary>
    [Fact]
    public async Task Linking_FollowsTheAccountType_LocalByMachine_DomainByItsDomain()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var t = _token;
        // D-347: an account DNA couldn't resolve: its name and the "domain" are its SID; BlueTrack keeps one under that SID.
        var sid = $"S-1-5-21-{Random.Shared.Next(100000000, 999999999)}-{Random.Shared.Next(100000000, 999999999)}-1105";
        // BlueTrack accounts from another source, each with the address a rule matches on (or one it must not).
        var seeded = (await connection.QueryAsync<(string User, long Key)>($"""
            INSERT INTO dbo.fact_account (SourceSystemKey, SourceAccountId, AccountName, UserName, Address, IsDeleted, CreatedDate)
            OUTPUT inserted.UserName, inserted.AccountKey
            SELECT s.SourceSystemKey, v.Id, v.UserName, v.UserName, v.Address, 0, CAST(SYSUTCDATETIME() AS DATE)
            FROM dbo.dim_source_system s
            CROSS JOIN (VALUES
                (N'{t}-a', N'{t}_local',  N'SRV01'),                -- short name: matches SRV01.corp.example
                (N'{t}-b', N'{t}_other',  N'SRV01.other.example'),  -- a different full name: no match
                (N'{t}-c', N'{t}_dom',    N'CORP'),                 -- the domain's first part
                (N'{t}-d', N'{t}_svc',    N'elsewhere.example'),    -- another domain: no match
                (N'{sid}', N'{t}_sid',    NULL),                    -- kept under its SID (an accepted discovered account)
                (N'{t}-e', N'{t}_nosid',  N'corp.example'),         -- same name as the SID row would have: no match by name
                (N'{t}-f', N'{t}_sa',     N'db01.corp.example'),    -- a SQL login on its machine, ignoring case
                (N'{t}-g', N'{t}_dbdom',  N'corp.example'),         -- a Windows domain user in a database
                (N'{t}-h', N'{t}_grp',    N'corp.example')          -- a group: never linked
            ) v(Id, UserName, Address)
            WHERE s.SourceSystemCode = 'SELFHOSTED';
            """)).ToDictionary(r => r.User, r => r.Key);
        try
        {
            using var workbook = new XLWorkbook();
            var win = workbook.AddWorksheet("Windows Scan");
            string[] header = ["Machine Name", "Account Name", "Account Type"];
            for (var c = 0; c < header.Length; c++) win.Cell(1, c + 1).Value = header[c];
            (string Machine, string Account, string Type)[] winRows =
            [
                ("SRV01.corp.example", $"{t}_local", "Local"),
                ("SRV01.corp.example", $"{t}_other", "Local"),
                ("SRV01.corp.example", $"{t}_dom", "Domain: corp.example"),
                ("SRV02.corp.example", $"{t}_dom", "Domain: corp.example"),
                ("APP01.corp.example", $"{t}_svc", "Domain: corp.example"),
                ("SRV03.corp.example", sid, $"Domain: {sid}")
            ];
            for (var i = 0; i < winRows.Length; i++)
            {
                win.Cell(i + 2, 1).Value = winRows[i].Machine; win.Cell(i + 2, 2).Value = winRows[i].Account; win.Cell(i + 2, 3).Value = winRows[i].Type;
            }
            var db = workbook.AddWorksheet("Database Scan");
            string[] dbHeader = ["Machine Name", "Instance Name", "Account Name", "Account Type"];
            for (var c = 0; c < dbHeader.Length; c++) db.Cell(1, c + 1).Value = dbHeader[c];
            (string Account, string Type)[] dbRows =
            [
                ($"{t}_sa", "Database Sql Login"),
                ($@"CORP\{t}_dbdom", "Windows Domain User: corp.example"),
                ($@"CORP\{t}_grp", "Windows Domain Group: corp.example")
            ];
            for (var i = 0; i < dbRows.Length; i++)
            {
                db.Cell(i + 2, 1).Value = "DB01.CORP.EXAMPLE"; db.Cell(i + 2, 2).Value = "MSSQLSERVER"; db.Cell(i + 2, 3).Value = dbRows[i].Account; db.Cell(i + 2, 4).Value = dbRows[i].Type;
            }
            using var stream = new MemoryStream();
            workbook.SaveAs(stream);
            (await Client("TestUser.Admin").PostAsync("/api/admin/dna/import", Upload(stream.ToArray()))).EnsureSuccessStatusCode();

            var links = (await connection.QueryAsync<(string Machine, string Account, long? Tracked)>(
                "SELECT MachineName, AccountName, TrackedAccountKey FROM web.dna_account")).ToList();
            long? LinkOf(string machine, string account) => links.Single(l => l.Machine == machine && l.Account == account).Tracked;

            Assert.Equal(seeded[$"{t}_local"], LinkOf("SRV01.corp.example", $"{t}_local"));
            Assert.Null(LinkOf("SRV01.corp.example", $"{t}_other"));
            Assert.Equal(seeded[$"{t}_dom"], LinkOf("SRV01.corp.example", $"{t}_dom"));   // the domain account CyberArk has: kept for every host
            Assert.Equal(seeded[$"{t}_dom"], LinkOf("SRV02.corp.example", $"{t}_dom"));
            Assert.Null(LinkOf("APP01.corp.example", $"{t}_svc"));
            Assert.Equal(seeded[$"{t}_sid"], LinkOf("SRV03.corp.example", sid));   // by its SID
            Assert.True(await connection.QuerySingleAsync<bool>("SELECT IsUnresolvedSid FROM web.dna_account WHERE AccountName = @sid", new { sid }));
            Assert.Equal(1, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.dna_account WHERE IsUnresolvedSid = 1"));
            var flagged = (await Client("TestUser.Admin").GetFromJsonAsync<JsonArray>("/api/reports/dna-accounts?unresolvedSid=true&pageSize=100"))!;
            Assert.Equal([sid], flagged.Select(r => r!["accountName"]!.GetValue<string>()));
            Assert.True(flagged[0]!["isUnresolvedSid"]!.GetValue<bool>());
            Assert.Equal(seeded[$"{t}_sa"], LinkOf("DB01.CORP.EXAMPLE", $"{t}_sa"));
            Assert.Equal(seeded[$"{t}_dbdom"], LinkOf("DB01.CORP.EXAMPLE", $@"CORP\{t}_dbdom"));
            Assert.Null(LinkOf("DB01.CORP.EXAMPLE", $@"CORP\{t}_grp"));
            Assert.True(await connection.QuerySingleAsync<bool>("SELECT IsDomainAccount FROM web.dna_account WHERE AccountName = @n", new { n = $@"CORP\{t}_dbdom" }));
            Assert.False(await connection.QuerySingleAsync<bool>("SELECT IsDomainAccount FROM web.dna_account WHERE AccountName = @n", new { n = $"{t}_sa" }));
        }
        finally
        {
            await connection.ExecuteAsync("""
                UPDATE web.dna_account SET TrackedAccountKey = NULL WHERE TrackedAccountKey IN (SELECT AccountKey FROM dbo.fact_account WHERE SourceAccountId LIKE @t + '-%' OR SourceAccountId = @sid);
                DELETE FROM dbo.fact_account WHERE SourceAccountId LIKE @t + '-%' OR SourceAccountId = @sid;
                """, new { t, sid });
        }
    }

    [Fact]
    public async Task TrackingAccounts_MakesThemBlueTrackAccounts_OncePerIdentity_KeptAcrossImports()
    {
        await ImportAsync();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var keys = await KeysAsync(connection);
        // The local account, and the domain account as DNA found it on SRV01 only (it found it on SRV02 too).
        var chosen = new[] { keys[$"{_token}_local@SRV01.corp.example"], keys[$"{_token}_dom@SRV01.corp.example"] };

        var response = await Client("TestUser.Admin").PostAsJsonAsync("/api/reports/dna-accounts/track", new { dnaAccountKeys = chosen });
        response.EnsureSuccessStatusCode();
        Assert.Equal(2, (await response.Content.ReadFromJsonAsync<JsonObject>())!["tracked"]!.GetValue<int>());

        // Two BlueTrack accounts, each on its machine; the domain account on SRV02 isn't joined to the one on SRV01.
        var accounts = (await connection.QueryAsync<(string SourceId, string Name, string? Address, string? Domain, string Stage)>("""
            SELECT fa.SourceAccountId, fa.AccountName, fa.Address, fa.PlatformLogonDomain, s.StageName
            FROM dbo.fact_account fa
            JOIN dbo.dim_source_system ss ON ss.SourceSystemKey = fa.SourceSystemKey AND ss.SourceSystemCode = 'DNA'
            JOIN dbo.fact_account_progress p ON p.AccountKey = fa.AccountKey
            JOIN dbo.dim_blueprint_stage s ON s.StageKey = p.CurrentStageKey
            WHERE fa.UserName LIKE @t + '%' ORDER BY fa.SourceAccountId
            """, new { t = _token })).ToList();
        Assert.Equal([$"SRV01.corp.example|{_token}_dom", $"SRV01.corp.example|{_token}_local"], accounts.Select(a => a.SourceId));
        Assert.Equal(($"{_token}_local on SRV01.corp.example", "SRV01.corp.example"), (accounts[1].Name, accounts[1].Address));
        Assert.Equal(($"{_token}_dom on SRV01.corp.example", "corp.example", (string?)"SRV01.corp.example"), (accounts[0].Name, accounts[0].Domain, accounts[0].Address));
        Assert.Null(await connection.QuerySingleAsync<long?>("SELECT TrackedAccountKey FROM web.dna_account WHERE DnaAccountKey = @k",
            new { k = keys[$"{_token}_dom@SRV02.corp.example"] }));
        Assert.All(accounts, a => Assert.Equal("Discovered", a.Stage));

        // They're in the Account Progress list, and the DNA page links to them and says who tracked them.
        var listed = await Client("TestUser.Admin").GetFromJsonAsync<JsonArray>($"/api/account-progress?search={_token}&pageSize=100");
        Assert.Equal(2, listed!.Count);
        var page = (await Client("TestUser.Admin").GetFromJsonAsync<JsonArray>("/api/reports/dna-accounts?tracked=true&pageSize=100"))!;
        Assert.All(page.Where(r => r!["accountName"]!.GetValue<string>().StartsWith(_token)), r => Assert.Equal("TestUser.Admin", r!["trackedBy"]!.GetValue<string>()));

        // A new import replaces the DNA rows; they link to the same accounts again (by their DNA identity), nothing is made
        // twice, and the domain account on SRV02 still isn't linked to SRV01's (not by its name either).
        await ImportAsync();
        Assert.Equal([("SRV01.corp.example", true), ("SRV01.corp.example", true), ("SRV02.corp.example", false)],
            (await connection.QueryAsync<(string Machine, bool Tracked)>(
                "SELECT MachineName, CAST(CASE WHEN TrackedAccountKey IS NULL THEN 0 ELSE 1 END AS BIT) FROM web.dna_account WHERE AccountName IN (@a, @b) ORDER BY MachineName, AccountName",
                new { a = $"{_token}_local", b = $"{_token}_dom" })).ToList());
        Assert.Equal(2, await connection.QuerySingleAsync<int>(
            "SELECT COUNT(*) FROM dbo.fact_account fa JOIN dbo.dim_source_system s ON s.SourceSystemKey = fa.SourceSystemKey AND s.SourceSystemCode = 'DNA' WHERE fa.UserName LIKE @t + '%'",
            new { t = _token }));

        Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.Viewer").PostAsJsonAsync("/api/reports/dna-accounts/track", new { dnaAccountKeys = chosen })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Client("TestUser.Admin").PostAsJsonAsync("/api/reports/dna-accounts/track", new { dnaAccountKeys = Array.Empty<int>() })).StatusCode);
    }

    [Fact]
    public async Task ATrackingRule_TracksWhatItMatches_AfterEachImport()
    {
        var admin = Client("TestUser.Admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/dna/track-rules", new { ruleName = $"{_token} empty", conditions = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/admin/dna/track-rules",
            new { ruleName = $"{_token} bad", conditions = new[] { new { fieldName = "Password", @operator = "Equals", value = "x" } } })).StatusCode);

        var created = await admin.PostAsJsonAsync("/api/admin/dna/track-rules", new
        {
            ruleName = $"{_token} service accounts", isEnabled = true,
            conditions = new[] { new { fieldName = "Finding", @operator = "Equals", value = "domain scan" }, new { fieldName = "AccountName", @operator = "StartsWith", value = _token } }
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        var outcome = await ImportAsync();
        Assert.Equal(1, outcome["trackedByRules"]!.GetValue<int>());
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        Assert.Equal($"Rule: {_token} service accounts", await connection.QuerySingleAsync<string>(
            "SELECT TrackedBy FROM web.dna_account WHERE AccountName = @n", new { n = $"{_token}_svc" }));

        var rules = (await admin.GetFromJsonAsync<JsonObject>("/api/admin/dna/track-rules"))!;
        var rule = rules["rules"]!.AsArray().Single(r => r!["ruleName"]!.GetValue<string>() == $"{_token} service accounts")!;
        Assert.Equal(0, rule["matchingNow"]!.GetValue<int>());   // tracked already
        Assert.Contains(rules["fields"]!.AsArray(), f => f!["fieldName"]!.GetValue<string>() == "LastLogin");
    }

    [Fact]
    public async Task Purge_DeletesTheDnaData_ButNotTheAccountsTracked_AndRetentionDeletesOldImports()
    {
        await ImportAsync();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var keys = await KeysAsync(connection);
        await Client("TestUser.Admin").PostAsJsonAsync("/api/reports/dna-accounts/track", new { dnaAccountKeys = new[] { keys[$"{_token}_sa@DB01.corp.example"] } });

        Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.Viewer").PostAsync("/api/admin/dna/purge", null)).StatusCode);
        var purged = await Client("TestUser.Admin").PostAsync("/api/admin/dna/purge", null);
        purged.EnsureSuccessStatusCode();
        Assert.Equal(0, await connection.QuerySingleAsync<int>("SELECT (SELECT COUNT(*) FROM web.dna_account) + (SELECT COUNT(*) FROM web.dna_import)"));
        Assert.Equal(1, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.fact_account WHERE UserName = @n AND IsDeleted = 0", new { n = $"{_token}_sa" }));
        Assert.True(await connection.QuerySingleAsync<bool>("SELECT CASE WHEN EXISTS (SELECT 1 FROM web.audit_event e JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey WHERE t.EventTypeName = 'DnaPurged' AND e.OccurredAt > DATEADD(MINUTE, -5, SYSUTCDATETIME())) THEN 1 ELSE 0 END"));

        // Retention: an import made 40 days ago is deleted with a 30-day period; one from today stays.
        await ImportAsync();
        await connection.ExecuteAsync("UPDATE web.dna_import SET ImportedAt = DATEADD(DAY, -40, SYSUTCDATETIME())");
        await ImportAsync();
        var (imports, accounts) = await new DnaRepository(new TestDbConnectionFactory()).PurgeOlderThanAsync(30);
        Assert.Equal(1, imports);
        Assert.Equal(0, accounts);   // each import replaces the accounts, so the old one had none left
        Assert.Equal(1, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.dna_import"));
    }

    [Fact]
    public async Task RestrictedColumns_AreBlankedForSomeoneWithoutThePermission_NotSearched_AndSortingWorks()
    {
        await ImportAsync();
        var admin = Client("TestUser.Admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/dna/settings", new { retentionDays = 0, restrictedColumns = Array.Empty<string>() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/admin/dna/settings", new { retentionDays = 30, restrictedColumns = new[] { "AccountName" } })).StatusCode);
        var saved = await admin.PutAsJsonAsync("/api/admin/dna/settings", new { retentionDays = 30, restrictedColumns = new[] { "MachineName", "OsVersion" } });
        saved.EnsureSuccessStatusCode();
        Assert.Equal(["MachineName", "OsVersion"], (await saved.Content.ReadFromJsonAsync<DnaSettings>())!.RestrictedColumns);

        // A role with ViewDiscoveredAccounts but not ViewDnaSensitiveColumns, for a test identity of its own.
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var identity = $"TestUser.{_token}";
        var roleKey = await connection.QuerySingleAsync<int>("""
            INSERT INTO web.app_role (RoleName) OUTPUT inserted.AppRoleKey VALUES (@name);
            """, new { name = $"{_token} DNA viewer" });
        await connection.ExecuteAsync("""
            INSERT INTO web.role_permission (RoleKey, PermissionKey)
            SELECT @roleKey, PermissionKey FROM web.app_permission WHERE PermissionName IN ('ViewDashboard', 'ViewDiscoveredAccounts');
            INSERT INTO web.identity_group_role_map (ProviderKey, IdentityGroupName, AppRoleKey)
            SELECT ProviderKey, @identity, @roleKey FROM web.identity_provider_config WHERE ProviderType = 'DevFakeAuth';
            """, new { roleKey, identity });
        try
        {
            var viewer = Client(identity);
            var response = await viewer.GetAsync($"/api/reports/dna-accounts?search={_token}_local&pageSize=100");
            Assert.Equal("MachineName,OsVersion", response.Headers.GetValues("X-Hidden-Columns").Single());
            var row = (await response.Content.ReadFromJsonAsync<JsonArray>())!.Single()!;
            Assert.Null(row["machineName"]);
            Assert.Null(row["osVersion"]);
            Assert.Equal("Server", row["machineType"]!.GetValue<string>());
            // A hidden column isn't searched, so its value can't be guessed through the search box.
            Assert.Empty((await viewer.GetFromJsonAsync<JsonArray>("/api/reports/dna-accounts?search=SRV01&pageSize=100"))!);

            var adminRow = (await admin.GetFromJsonAsync<JsonArray>($"/api/reports/dna-accounts?search={_token}_local&pageSize=100"))!.Single()!;
            Assert.Equal("SRV01.corp.example", adminRow["machineName"]!.GetValue<string>());
        }
        finally
        {
            await connection.ExecuteAsync("""
                DELETE FROM web.identity_group_role_map WHERE AppRoleKey = @roleKey;
                DELETE FROM web.role_permission WHERE RoleKey = @roleKey; DELETE FROM web.app_role WHERE AppRoleKey = @roleKey;
                """, new { roleKey });
        }

        // D-341: sorting by a column, then the default order.
        var sorted = (await admin.GetFromJsonAsync<JsonArray>($"/api/reports/dna-accounts?search={_token}&sort=AccountName:desc&pageSize=100"))!
            .Select(r => r!["accountName"]!.GetValue<string>()).ToList();
        Assert.Equal(sorted.OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase).ToList(), sorted);
    }

    [Fact]
    public async Task ADnaFeed_ImportsTheWorkbookFromAFolder()
    {
        var admin = Client("TestUser.Admin");
        var folder = Directory.CreateTempSubdirectory("BlueTrackDnaFeed_").FullName;
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(folder, "dna-report.xlsx"), Workbook());
            var test = (await (await admin.PostAsJsonAsync("/api/admin/data-feeds/test",
                new { feedType = "DnaReport", folderPath = folder, fileNamePattern = "dna-report.xlsx" })).Content.ReadFromJsonAsync<JsonObject>())!;
            Assert.Contains("5 accounts (Windows Scan=3; Domain Scan=1; Database Scan=1) and 2 hard-coded credentials.", test["message"]!.GetValue<string>());

            var created = await admin.PostAsJsonAsync("/api/admin/data-feeds",
                new { displayName = $"{_token} DNA", feedType = "DnaReport", folderPath = folder, fileNamePattern = "dna-report.xlsx", isEnabled = false, displayOrder = 970 });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var key = (await created.Content.ReadFromJsonAsync<JsonObject>())!["dataFeedKey"]!.GetValue<int>();
            try
            {
                Assert.Equal(HttpStatusCode.Accepted, (await admin.PostAsJsonAsync("/api/admin/data-feeds/run", new { dataFeedKeys = new[] { key } })).StatusCode);
                JsonObject? run = null;
                for (var i = 0; i < 100 && run is null; i++)
                {
                    await Task.Delay(200);
                    if (!(await admin.GetFromJsonAsync<JsonObject>("/api/admin/data-feeds/status"))!["isRunning"]!.GetValue<bool>())
                        run = (await admin.GetFromJsonAsync<JsonArray>($"/api/admin/data-feeds/{key}/runs"))![0]!.AsObject();
                }
                Assert.NotNull(run);
                Assert.Equal(("Succeeded", 5), (run!["outcome"]!.GetValue<string>(), run["totalRows"]!.GetValue<int>()));
                Assert.StartsWith("5 accounts (Windows Scan=3", run["summary"]!.GetValue<string>());
                var latest = (await admin.GetFromJsonAsync<JsonObject>("/api/admin/dna"))!["latest"]!;
                Assert.Equal("dna-report.xlsx", latest["fileName"]!.GetValue<string>());
            }
            finally
            {
                await using var connection = new SqlConnection(TestDatabase.ConnectionString);
                await connection.ExecuteAsync("DELETE FROM web.data_feed_run WHERE DataFeedKey = @key; DELETE FROM web.data_feed WHERE DataFeedKey = @key", new { key });
            }
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
