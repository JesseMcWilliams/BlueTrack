using System.Net;
using System.Text;
using BlueTrack.Api.CyberArkDirect;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-248: the PVWA REST client against a fake PVWA (the shapes were captured from the lab on 2026-10-19).</summary>
public class PvwaClientTests
{
    private const string Token = "TOKEN-VALUE";
    private static readonly Uri Root = PvwaClient.ApiRootFor("https://pvwa.example.com");

    /// <summary>Answers by path (and query when the key has one); records every request.</summary>
    private sealed class FakePvwa : HttpMessageHandler
    {
        public Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> Routes { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<(HttpMethod Method, string PathAndQuery, string? Authorization, string? Body)> Requests { get; } = [];

        public FakePvwa Json(string pathAndQuery, string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            Routes[pathAndQuery] = _ => new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            return this;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (Requests) Requests.Add((request.Method, request.RequestUri.PathAndQuery, request.Headers.TryGetValues("Authorization", out var a) ? a.Single() : null, body));
            // Try the full path and query, then the path alone.
            if (Routes.TryGetValue(request.RequestUri.PathAndQuery, out var exact)) return exact(request);
            if (Routes.TryGetValue(path, out var byPath)) return byPath(request);
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{\"ErrorMessage\":\"no route in the fake\"}") };
        }
    }

    private static PvwaClient Client(FakePvwa fake) => new(new HttpClient(fake), Root);

    private static string Accounts(params string[] ids) =>
        "{\"value\":[" + string.Join(",", ids.Select(id =>
            $"{{\"id\":\"{id}\",\"name\":\"acct-{id}\",\"address\":\"host\",\"userName\":\"u\",\"platformId\":\"UnixSSH\",\"safeName\":\"S\"," +
            "\"platformAccountProperties\":{\"LogonDomain\":\"CORP\"},\"secretManagement\":{\"automaticManagementEnabled\":true,\"lastModifiedTime\":1785196571}," +
            "\"createdTime\":1785188373}")) + "]}";

    /// <summary>A PVWA that answers the calls a pull makes with one of everything.</summary>
    private static FakePvwa FullPvwa()
    {
        var fake = new FakePvwa();
        fake.Json("/PasswordVault/API/Platforms", "{\"Platforms\":[{\"general\":{\"id\":\"UnixSSH\",\"name\":\"Unix via SSH\",\"active\":true,\"platformType\":\"Regular\"}}],\"Total\":1}");
        fake.Json("/PasswordVault/API/Safes", "{\"value\":[{\"safeUrlId\":\"My%20Safe\",\"safeName\":\"My Safe\",\"safeNumber\":59,\"location\":\"\\\\\",\"creator\":{\"id\":\"2\",\"name\":\"Administrator\"},\"creationTime\":1757462738,\"numberOfDaysRetention\":30,\"numberOfVersionsRetention\":null}]}");
        fake.Json("/PasswordVault/API/Accounts", Accounts("59_3", "59_4"));
        fake.Json("/PasswordVault/API/Safes/My%20Safe/Members", "{\"value\":[{\"memberId\":2,\"memberName\":\"Administrator\",\"memberType\":\"User\",\"membershipExpirationDate\":null,\"permissions\":{\"listAccounts\":true,\"retrieveAccounts\":false}}]}");
        fake.Json("/PasswordVault/API/Users", "{\"Users\":[{\"id\":47,\"username\":\"adm.jesse\",\"source\":\"LDAP\",\"userType\":\"EPVUser\",\"location\":\"\\\\\",\"enableUser\":true,\"personalDetails\":{\"firstName\":\"Jesse\",\"lastName\":\"Smith\"}}],\"Total\":1}");
        fake.Json("/PasswordVault/API/Users/47/", "{\"internet\":{\"businessEmail\":\"jesse@example.com\"}}");
        fake.Json("/PasswordVault/API/UserGroups", "{\"value\":[{\"id\":9,\"groupName\":\"Auditors\",\"groupType\":\"Vault\",\"location\":\"\\\\\",\"members\":[{\"username\":\"Auditor\",\"id\":3}]}]}");
        return fake;
    }

    [Theory]
    [InlineData("https://pvwa.example.com", "https://pvwa.example.com/PasswordVault/API/")]
    [InlineData("https://pvwa.example.com/", "https://pvwa.example.com/PasswordVault/API/")]
    [InlineData("https://pvwa.example.com/PasswordVault", "https://pvwa.example.com/PasswordVault/API/")]
    [InlineData("https://pvwa.example.com/passwordvault/", "https://pvwa.example.com/PasswordVault/API/")]
    public void ApiRootFor_AcceptsTheAddressInEitherForm(string input, string expected) =>
        Assert.Equal(expected, PvwaClient.ApiRootFor(input).ToString());

    [Fact]
    public async Task Logon_PostsUsernameAndPassword_AndReturnsTheTokenFromAJsonString()
    {
        var fake = new FakePvwa().Json("/PasswordVault/API/auth/Cyberark/Logon/", "\"" + Token + "\"");

        var token = await Client(fake).LogonAsync("Cyberark", "CA_User", "p@ss\"word", CancellationToken.None);

        Assert.Equal(Token, token);
        var request = Assert.Single(fake.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Contains("\"username\":\"CA_User\"", request.Body);
        Assert.Contains("\"password\":\"p@ss\\u0022word\"", request.Body);
        Assert.Contains("\"concurrentSession\":true", request.Body);
    }

    [Fact]
    public async Task Logon_UsesTheChosenMethodsUrl()
    {
        var fake = new FakePvwa().Json("/PasswordVault/API/auth/LDAP/Logon/", "\"t\"");

        await Client(fake).LogonAsync("LDAP", "u", "p", CancellationToken.None);

        Assert.Equal("/PasswordVault/API/auth/LDAP/Logon/", fake.Requests[0].PathAndQuery);
    }

    [Fact]
    public async Task Logon_Refused_SaysTheCredentialWasRefused_WithoutTheirPassword()
    {
        var fake = new FakePvwa().Json("/PasswordVault/API/auth/Cyberark/Logon/", "{\"ErrorCode\":\"ITATS003E\",\"ErrorMessage\":\"Authentication failure for User secret-user.\"}", HttpStatusCode.Unauthorized);

        var error = await Assert.ThrowsAsync<PvwaException>(() => Client(fake).LogonAsync("Cyberark", "secret-user", "hunter2", CancellationToken.None));

        Assert.Contains("the credential was refused", error.Message);
        Assert.DoesNotContain("hunter2", error.Message);
    }

    [Fact]
    public async Task Pull_ReadsEverything_IntoTheSnapshot()
    {
        var fake = FullPvwa();

        var snapshot = await Client(fake).PullAsync(Token, CancellationToken.None);

        var platform = Assert.Single(snapshot.Platforms);
        Assert.Equal(("UnixSSH", "Unix via SSH", true, "Regular"), (platform.Id, platform.Name, platform.Active, platform.PlatformType));

        var safe = Assert.Single(snapshot.Safes);
        Assert.Equal((59L, "My%20Safe", "My Safe", "Administrator", 30), (safe.SafeNumber, safe.SafeUrlId, safe.SafeName, safe.CreatorName, safe.DaysRetention));
        Assert.Null(safe.VersionsRetention);
        Assert.Equal(new DateTime(2025, 9, 10, 0, 5, 38, DateTimeKind.Utc), safe.CreationTime);

        Assert.Equal(["59_3", "59_4"], snapshot.Accounts.Select(a => $"{a.SafeNumber}_{a.FileId}"));
        var account = snapshot.Accounts[0];
        Assert.Equal(("acct-59_3", "host", "u", "UnixSSH", true), (account.Name, account.Address, account.UserName, account.PlatformId, account.AutoManaged));
        Assert.Equal("CORP", account.Properties["LogonDomain"]);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1785196571).UtcDateTime, account.LastModifiedTime);

        var member = Assert.Single(snapshot.Members);
        Assert.Equal((59L, 2L, "Administrator", false), (member.SafeNumber, member.MemberId, member.MemberName, member.IsGroup));
        Assert.True(member.Permissions["listAccounts"]);
        Assert.False(member.Permissions["retrieveAccounts"]);

        var user = Assert.Single(snapshot.Users);
        Assert.Equal((47L, "adm.jesse", "LDAP", "Jesse", "Smith", "jesse@example.com"), (user.Id, user.UserName, user.Source, user.FirstName, user.LastName, user.Email));

        var group = Assert.Single(snapshot.Groups);
        Assert.Equal((9L, "Auditors"), (group.Id, group.Name));
        Assert.Equal([3L], group.MemberIds);
        Assert.Empty(snapshot.Warnings);
    }

    [Fact]
    public async Task Pull_SendsTheTokenOnEveryCall_AndNeverReadsDiscoveredAccounts()
    {
        var fake = FullPvwa();

        await Client(fake).PullAsync(Token, CancellationToken.None);

        Assert.All(fake.Requests, r => Assert.Equal(Token, r.Authorization));
        Assert.All(fake.Requests, r => Assert.Equal(HttpMethod.Get, r.Method));
        Assert.DoesNotContain(fake.Requests, r => r.PathAndQuery.Contains("DiscoveredAccounts", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Accounts_ArePagedUntilAShortPage()
    {
        var fake = FullPvwa();
        var full = Enumerable.Range(1, 1000).Select(i => "59_" + i).ToArray();
        fake.Routes["/PasswordVault/API/Accounts?limit=1000&offset=0"] = _ => Ok(Accounts(full));
        fake.Routes["/PasswordVault/API/Accounts?limit=1000&offset=1000"] = _ => Ok(Accounts("60_1", "60_2"));

        var snapshot = await Client(fake).PullAsync(Token, CancellationToken.None);

        Assert.Equal(1002, snapshot.Accounts.Count);
        Assert.Equal(2, fake.Requests.Count(r => r.PathAndQuery.StartsWith("/PasswordVault/API/Accounts?")));
    }

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task AccountIdThatIsNotSafeNumberUnderscoreFileId_IsSkippedWithAWarning()
    {
        var fake = FullPvwa();
        fake.Json("/PasswordVault/API/Accounts", Accounts("59_3", "weird-id", "x_y"));

        var snapshot = await Client(fake).PullAsync(Token, CancellationToken.None);

        Assert.Equal(["59_3"], snapshot.Accounts.Select(a => $"{a.SafeNumber}_{a.FileId}"));
        Assert.Equal(2, snapshot.Warnings.Count(w => w.Contains("isn't SafeNumber_FileId")));
    }

    [Fact]
    public async Task SafeMembersRefused403_IsAWarning_NotAFailure()
    {
        var fake = FullPvwa();
        fake.Json("/PasswordVault/API/Safes/My%20Safe/Members", "{\"ErrorMessage\":\"Not allowed\"}", HttpStatusCode.Forbidden);

        var snapshot = await Client(fake).PullAsync(Token, CancellationToken.None);

        Assert.Empty(snapshot.Members);
        Assert.Contains("Safe 'My Safe': members not read", Assert.Single(snapshot.Warnings));
    }

    [Fact]
    public async Task SafeMembersFailingWithAServerError_StopsThePull()
    {
        var fake = FullPvwa();
        fake.Json("/PasswordVault/API/Safes/My%20Safe/Members", "{\"ErrorMessage\":\"boom\"}", HttpStatusCode.InternalServerError);

        var error = await Assert.ThrowsAsync<PvwaException>(() => Client(fake).PullAsync(Token, CancellationToken.None));

        Assert.Contains("(500)", error.Message);
        Assert.Contains("boom", error.Message);
    }

    [Fact]
    public async Task UserDetailsFailing_LosesOnlyThatEmail_WithAWarning()
    {
        var fake = FullPvwa();
        fake.Json("/PasswordVault/API/Users/47/", "{}", HttpStatusCode.InternalServerError);

        var snapshot = await Client(fake).PullAsync(Token, CancellationToken.None);

        var user = Assert.Single(snapshot.Users);
        Assert.Null(user.Email);
        Assert.Contains("User 'adm.jesse': details not read", Assert.Single(snapshot.Warnings));
    }

    [Fact]
    public async Task UsersListShorterThanItsTotal_IsAnIncompleteListAndStopsThePull()
    {
        var fake = FullPvwa();
        fake.Json("/PasswordVault/API/Users", "{\"Users\":[{\"id\":47,\"username\":\"a\"}],\"Total\":6001}");

        var error = await Assert.ThrowsAsync<PvwaException>(() => Client(fake).PullAsync(Token, CancellationToken.None));

        Assert.Contains("reported 6001 users but returned 1", error.Message);
    }

    [Fact]
    public async Task PlatformWithoutAnId_IsSkipped_AndTheAccountsPlatformIdIsKeptAsIs()
    {
        var fake = FullPvwa();
        fake.Json("/PasswordVault/API/Platforms", "{\"Platforms\":[{\"general\":{\"name\":\"no id\"}},{\"general\":{\"id\":\"PSMSecureConnect\",\"name\":\"\",\"active\":false}}],\"Total\":2}");

        var snapshot = await Client(fake).PullAsync(Token, CancellationToken.None);

        Assert.Equal(["PSMSecureConnect"], snapshot.Platforms.Select(p => p.Id));
        Assert.Equal("UnixSSH", snapshot.Accounts[0].PlatformId);
    }

    [Fact]
    public async Task AccountTimes_ZeroNegativeOrYearOne_AreNull()
    {
        var fake = FullPvwa();
        fake.Json("/PasswordVault/API/Accounts",
            "{\"value\":[{\"id\":\"59_3\",\"name\":\"n\",\"createdTime\":0,\"secretManagement\":{\"lastModifiedTime\":-62135596800,\"lastVerifiedDateTime\":1785196571}}]}");

        var account = Assert.Single((await Client(fake).PullAsync(Token, CancellationToken.None)).Accounts);

        Assert.Null(account.CreatedTime);
        Assert.Null(account.LastModifiedTime);
        Assert.NotNull(account.LastVerifiedTime);
    }

    [Fact]
    public async Task ManualManagementReason_IsKeptWhenAutoManagementIsOff()
    {
        var fake = FullPvwa();
        fake.Json("/PasswordVault/API/Accounts",
            "{\"value\":[{\"id\":\"59_3\",\"name\":\"n\",\"secretManagement\":{\"automaticManagementEnabled\":false,\"manualManagementReason\":\"Owned by DBAs\"}}]}");

        var account = Assert.Single((await Client(fake).PullAsync(Token, CancellationToken.None)).Accounts);

        Assert.False(account.AutoManaged);
        Assert.Equal("Owned by DBAs", account.ManualReason);
    }

    [Fact]
    public async Task Unreachable_SaysSoInPlainWords()
    {
        var handler = new ThrowingHandler(new HttpRequestException("No such host is known."));

        var error = await Assert.ThrowsAsync<PvwaException>(() => new PvwaClient(new HttpClient(handler), Root).LogonAsync("Cyberark", "u", "p", CancellationToken.None));

        Assert.Contains("Couldn't reach PVWA to log on", error.Message);
    }

    [Fact]
    public async Task Logoff_SwallowsAConnectionFailure()
    {
        var handler = new ThrowingHandler(new HttpRequestException("gone"));

        await new PvwaClient(new HttpClient(handler), Root).LogoffAsync(Token, CancellationToken.None);   // must not throw
    }

    private sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => throw exception;
    }
}
