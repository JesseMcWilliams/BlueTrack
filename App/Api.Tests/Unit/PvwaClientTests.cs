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
        fake.Json("/PasswordVault/API/Platforms/Targets", "{\"Platforms\":[],\"Total\":0}");
        return fake;
    }

    /// <summary>D-270: the full shapes the lab's PVWA 14.6 returned on 2026-10-20 (values shortened).</summary>
    private static FakePvwa RichPvwa()
    {
        var fake = FullPvwa();
        fake.Json("/PasswordVault/API/Platforms", """
            {"Platforms":[{"general":{"id":"LNX_LOC_JLM_PW","name":"Linux local","systemType":"*NIX","active":true,"description":"d","platformBaseID":"UnixSSH","platformType":"regular"},
              "properties":{"required":[{"name":"Address","displayName":"Address"}],"optional":[{"name":"UseSudoOnReconcile","displayName":"Use Sudo On Reconcile"}]},
              "linkedAccounts":[{"name":"ReconcileAccount","displayName":"Reconcile Account"}],
              "credentialsManagement":{"allowedSafes":".*","allowManualChange":true,"performPeriodicChange":false,"requirePasswordChangeEveryXDays":90,
                "allowManualVerification":true,"performPeriodicVerification":true,"requirePasswordVerificationEveryXDays":7,
                "allowManualReconciliation":true,"automaticReconcileWhenUnsynched":false},
              "sessionManagement":{"requirePrivilegedSessionMonitoringAndIsolation":true,"recordAndSaveSessionActivity":true,"PSMServerID":"PSMServer_X"},
              "privilegedAccessWorkflows":{"requireDualControlPasswordAccessApproval":false,"enforceCheckinCheckoutExclusiveAccess":true,"enforceOnetimePasswordAccess":false}}],"Total":1}
            """);
        fake.Json("/PasswordVault/API/Platforms/Targets", """
            {"Platforms":[{"ID":21,"PlatformID":"LNX_LOC_JLM_PW","CredentialsManagementPolicy":{"Verification":{"AutoOnAdd":true},"Change":{"AutoOnAdd":false},
              "SecretUpdateConfiguration":{"ChangePasswordInResetMode":false}},
              "PrivilegedAccessWorkflows":{"RequireUsersToSpecifyReasonForAccess":{"IsActive":true,"IsAnException":false}}}],"Total":1}
            """);
        fake.Json("/PasswordVault/API/Safes", """
            {"value":[{"safeUrlId":"My%20Safe","safeName":"My Safe","safeNumber":59,"location":"\\","description":"Ops","creator":{"id":"2","name":"Administrator"},
              "olacEnabled":false,"managingCPM":"PasswordManager","numberOfVersionsRetention":5,"numberOfDaysRetention":30,"autoPurgeEnabled":false,
              "creationTime":1757462738,"lastModificationTime":1792455138631415,"isExpiredMember":false}]}
            """);
        fake.Json("/PasswordVault/API/Accounts", """
            {"value":[
              {"id":"60_6","name":"rec","address":"192.0.2.152","userName":"CyberArkSHRec","platformId":"LNX_LOC_JLM_PW","safeName":"My Safe","secretType":"password",
               "createdTime":1789088250,"categoryModificationTime":1792455167,"platformAccountProperties":{"LogonDomain":"Company","AVeryLongPropertyNameOverTwentyNine":"kept"},
               "secretManagement":{"automaticManagementEnabled":true,"status":"success","lastModifiedTime":1792455158,"lastReconciledTime":1792455153},
               "remoteMachinesAccess":{"remoteMachines":"a;b","accessRestrictedToRemoteMachines":true}},
              {"id":"60_7","name":"never","address":"h","userName":"u","platformId":"UnixSSH","safeName":"My Safe","secretType":"key",
               "secretManagement":{"automaticManagementEnabled":true,"lastModifiedTime":1789088251}}]}
            """);
        fake.Json("/PasswordVault/WebServices/PIMServices.svc/Accounts/60_6/Activities/", """
            {"GetAccountActivitiesSlashResult":[
              {"ActivityCode":295,"Activity":"Retrieve password","Time":"10/20/2026 00:24:45","UserName":"PasswordManager","Reason":"CPM"},
              {"ActivityCode":22,"Activity":"CPM Verify Password","Time":"10/20/2026 00:12:47","UserName":"PasswordManager","Reason":"VerificationPeriod","MoreInfo":"address=192.0.2.152"},
              {"ActivityCode":24,"Activity":"CPM Change Password","Time":"10/20/2026 00:12:41","UserName":"PasswordManager","Reason":"ImmediateTask"},
              {"ActivityCode":22,"Activity":"CPM Verify Password","Time":"10/13/2026 08:00:00","UserName":"PasswordManager","Reason":"older"}]}
            """);
        fake.Json("/PasswordVault/API/Safes/My%20Safe/Members", """
            {"value":[
              {"memberId":2,"memberName":"Administrator","memberType":"User","isPredefinedUser":true,"isReadOnly":true,"isExpiredMembershipEnable":false,
               "membershipExpirationDate":null,"permissions":{"listAccounts":true,"viewAuditLog":true,"requestsAuthorizationLevel2":false}},
              {"memberId":47,"memberName":"adm.jesse","memberType":"User","isPredefinedUser":false,"isReadOnly":false,"isExpiredMembershipEnable":false,
               "membershipExpirationDate":1800000000,"permissions":{"useAccounts":true}}]}
            """);
        fake.Json("/PasswordVault/API/Users", """
            {"Users":[{"id":47,"username":"adm.jesse","source":"LDAP","userType":"EPVUser","componentUser":false,"location":"\\","enableUser":true,"suspended":false,
              "vaultAuthorization":["AuditUsers"],"allowedAuthenticationMethods":["saml"],"personalDetails":{"firstName":"Jesse","middleName":"Q","lastName":"Smith"}}],"Total":1}
            """);
        fake.Json("/PasswordVault/API/Users/47/", """
            {"id":47,"userDN":"CN=Jesse,DC=example,DC=com","description":"admin","expiryDate":-62135568000,"lastSuccessfulLoginDate":1792455000,
             "passwordNeverExpires":false,"changePassOnNextLogon":false,"suspended":false,"vaultAuthorization":["AuditUsers","AddSafes"],
             "unAuthorizedInterfaces":["PSMP"],"authenticationMethod":["AuthTypeLDAP"],
             "internet":{"businessEmail":"jesse@example.com","homeEmail":"","otherEmail":"o@example.com","homePage":""},
             "personalDetails":{"organization":"IT","department":"PAM","title":"Engineer","profession":"","street":"1 Main St"},
             "phones":{"businessNumber":"555"},"businessAddress":{"workCity":"Springfield"}}
            """);
        fake.Json("/PasswordVault/API/UserGroups", """
            {"value":[{"id":30,"groupName":"CyberArk Vault Admins","groupType":"Directory","location":"\\","description":"","directory":"Company.com",
              "dn":"CN=CyberArk Vault Admins,DC=example,DC=com","members":[{"id":47,"username":"adm.jesse"}]}]}
            """);
        fake.Json(ApplicationsPath, """{"application":[]}""");   // D-282: read only by a real pull (PvwaPullOptions)
        return fake;
    }

    [Fact]
    public async Task Pull_KeepsEveryFieldRestReturns()
    {
        var snapshot = await Client(RichPvwa()).PullAsync(Token, CancellationToken.None);

        var platform = Assert.Single(snapshot.Platforms);
        Assert.Equal(("*NIX", "UnixSSH", ".*"), (platform.SystemType, platform.PlatformBaseId, platform.AllowedSafes));
        Assert.Equal(((bool?)true, (int?)7, (bool?)false, (int?)90), (platform.PerformPeriodicVerification, platform.VerifyEveryXDays, platform.PerformPeriodicChange, platform.ChangeEveryXDays));
        Assert.Equal(((bool?)true, (bool?)true, "PSMServer_X", (bool?)true), (platform.RequirePsm, platform.RecordSessions, platform.PsmServerId, platform.EnforceExclusiveAccess));
        Assert.Equal(((bool?)true, (bool?)true, (bool?)false, (bool?)false), (platform.RequireReasonForAccess, platform.VerifyOnAdd, platform.ChangeOnAdd, platform.ChangePasswordInResetMode));
        Assert.Equal([("Required", "Address", "Address"), ("Optional", "UseSudoOnReconcile", "Use Sudo On Reconcile"), ("LinkedAccount", "ReconcileAccount", "Reconcile Account")],
            platform.Properties);

        var safe = Assert.Single(snapshot.Safes);
        Assert.Equal(("Ops", "2", "PasswordManager", (bool?)false, (bool?)false), (safe.Description, safe.CreatorId, safe.ManagingCpm, safe.OlacEnabled, safe.IsExpiredMember));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792455138).UtcDateTime, safe.LastModificationTime);   // sent in microseconds

        var rec = snapshot.Accounts.Single(a => a.Id == "60_6");
        Assert.Equal(("password", "success", "a;b", (bool?)true), (rec.SecretType, rec.CpmStatus, rec.RemoteMachines, rec.AccessRestrictedToRemoteMachines));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792455153).UtcDateTime, rec.LastReconciledTime);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792455167).UtcDateTime, rec.CategoryModificationTime);
        Assert.Null(rec.LastVerifiedTime);   // the lab never sent it
        Assert.Equal("kept", rec.Properties["AVeryLongPropertyNameOverTwentyNine"]);
        var never = snapshot.Accounts.Single(a => a.Id == "60_7");
        Assert.Null(never.CpmStatus);
        Assert.Empty(never.CpmActivity);

        var admin = snapshot.Members.Single(m => m.MemberId == 2);
        Assert.Equal(("My%20Safe", "User", (bool?)true, (bool?)true), (admin.SafeUrlId, admin.MemberType, admin.IsPredefinedUser, admin.IsReadOnly));
        Assert.True(admin.Permissions["viewAuditLog"]);

        var user = Assert.Single(snapshot.Users);
        Assert.True(user.DetailsRead);
        Assert.Equal(("Q", "IT", "PAM", "Engineer", "o@example.com"), (user.MiddleName, user.Organization, user.Department, user.Title, user.OtherEmail));
        Assert.Equal(("CN=Jesse,DC=example,DC=com", "admin", (bool?)false), (user.UserDn, user.Description, user.PasswordNeverExpires));
        Assert.Null(user.ExpiryDate);   // year 1: never
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1792455000).UtcDateTime, user.LastSuccessfulLoginDate);
        Assert.Equal([("VaultAuthorization", "AuditUsers"), ("VaultAuthorization", "AddSafes"), ("UnauthorizedInterface", "PSMP"),
            ("AuthenticationMethod", "AuthTypeLDAP"), ("AllowedAuthenticationMethod", "saml")], user.Authorizations);

        var group = Assert.Single(snapshot.Groups);
        Assert.Equal(("Company.com", "CN=CyberArk Vault Admins,DC=example,DC=com", "adm.jesse"), (group.Directory, group.Dn, group.MemberNames[47]));
        Assert.Empty(snapshot.Warnings);
    }

    [Fact]
    public async Task CpmActivity_IsReadOnlyForAccountsTheCpmActedOn_KeepingTheLatestOfEachCpmCode()
    {
        var fake = RichPvwa();

        var snapshot = await Client(fake).PullAsync(Token, CancellationToken.None);

        var activityCalls = fake.Requests.Where(r => r.PathAndQuery.Contains("/Activities")).Select(r => r.PathAndQuery).ToList();
        Assert.Equal(["/PasswordVault/WebServices/PIMServices.svc/Accounts/60_6/Activities/"], activityCalls);   // 60_7 has no CPM status
        var activity = snapshot.Accounts.Single(a => a.Id == "60_6").CpmActivity;
        Assert.Equal([22, 24], activity.Select(a => a.Code));   // 295 (a retrieval) isn't a CPM action
        var verify = activity.Single(a => a.Code == 22);
        Assert.Equal(new DateTime(2026, 10, 20, 0, 12, 47, DateTimeKind.Utc), verify.Time);   // the later of the two verifies
        Assert.Equal(DateTimeKind.Utc, verify.Time!.Value.Kind);
        Assert.Equal(("CPM Verify Password", "PasswordManager", "VerificationPeriod", "address=192.0.2.152"), (verify.Activity, verify.UserName, verify.Reason, verify.MoreInfo));
    }

    [Fact]
    public async Task CpmActivityRefused_LosesOnlyThatAccountsHistory_WithAWarning()
    {
        var fake = RichPvwa();
        fake.Json("/PasswordVault/WebServices/PIMServices.svc/Accounts/60_6/Activities/", "{\"ErrorMessage\":\"Not allowed\"}", HttpStatusCode.Forbidden);

        var snapshot = await Client(fake).PullAsync(Token, CancellationToken.None);

        Assert.Empty(snapshot.Accounts.Single(a => a.Id == "60_6").CpmActivity);
        Assert.Equal(2, snapshot.Accounts.Count);
        Assert.Contains("Account '60_6': CPM activity not read", Assert.Single(snapshot.Warnings));
    }

    private const string ClassicSearch = "/PasswordVault/WebServices/PIMServices.svc/Accounts";

    /// <summary>D-280: the classic search as the lab answered it (2026-10-21): up to 10 accounts, Properties and InternalProperties as Key/Value lists.</summary>
    private const string ClassicRec = """
        {"Count":2,"accounts":[
          {"AccountID":"60_99","Properties":[{"Key":"DeviceType","Value":"Wrong One"}],"InternalProperties":[]},
          {"AccountID":"60_6","InternalProperties":[{"Key":"ExtraPass3Safe","Value":"Rec Safe"},{"Key":"ExtraPass3Folder","Value":"Root"},
             {"Key":"ExtraPass3Name","Value":"rec-acct"},{"Key":"ExtraPass1Safe","Value":"Logon Safe"},{"Key":"ExtraPass1Folder","Value":"Root\\Sub"},
             {"Key":"ExtraPass1Name","Value":"logon-acct"},{"Key":"CreationMethod","Value":"PVWA"},{"Key":"CPMStatus","Value":"success"}],
           "Properties":[{"Key":"Safe","Value":"My Safe"},{"Key":"UserName","Value":"CyberArkSHRec"},{"Key":"DeviceType","Value":"Operating System"}]}]}
        """;

    private static PvwaAccountDetail Cached(string id, DateTime? time, string deviceType) =>
        new(id, time, deviceType, "Old", null, null, null, null, null, null);

    [Fact]
    public async Task ClassicDetails_AreReadForNewOrChangedAccounts_ByUserNameAndAddress_MatchingTheAccountId()
    {
        var fake = RichPvwa();
        fake.Routes[ClassicSearch] = request => Ok(request.RequestUri!.Query.Contains("CyberArkSHRec") ? ClassicRec : """{"Count":0,"accounts":[]}""");
        var rec = DateTimeOffset.FromUnixTimeSeconds(1792455167).UtcDateTime;
        // 60_6 was read at an older time, so it changed; 60_7 has no CategoryModificationTime, so it is always read.
        var options = new PvwaPullOptions { CachedDetails = new Dictionary<string, PvwaAccountDetail> { ["60_6"] = Cached("60_6", rec.AddDays(-1), "Old type"), ["60_7"] = Cached("60_7", null, "Kept") } };

        var snapshot = await Client(fake).PullAsync(Token, CancellationToken.None, options);

        var calls = fake.Requests.Where(r => r.PathAndQuery.StartsWith(ClassicSearch + "?")).Select(r => r.PathAndQuery).ToList();
        Assert.Contains($"{ClassicSearch}?Keywords=CyberArkSHRec%20192.0.2.152&Safe=My%20Safe", calls);
        var detail = snapshot.Accounts.Single(a => a.Id == "60_6");
        Assert.True(detail.DetailReadThisPull);
        Assert.Equal(new PvwaAccountDetail("60_6", rec, "Operating System", "PVWA", "Logon Safe", "Root\\Sub", "logon-acct", "Rec Safe", "Root", "rec-acct"), detail.Detail);
        Assert.Equal([detail.Detail], options.FreshDetails);

        // 60_7 wasn't found by "u h", "u" or "h": what was read before is kept, with one warning, and it is tried again next time.
        Assert.Equal(3, calls.Count(c => c.Contains("Safe=My%20Safe") && !c.Contains("CyberArkSHRec")));
        var never = snapshot.Accounts.Single(a => a.Id == "60_7");
        Assert.Equal(("Kept", false), (never.Detail!.DeviceType, never.DetailReadThisPull));
        Assert.Contains("60_7 (not found by user name and address)", Assert.Single(snapshot.Warnings));
    }

    [Fact]
    public async Task ClassicDetails_AnUnchangedAccountIsNotAskedAgain_AndTestReadsNone()
    {
        var fake = RichPvwa();
        fake.Routes[ClassicSearch] = _ => Ok(ClassicRec);
        var rec = DateTimeOffset.FromUnixTimeSeconds(1792455167).UtcDateTime;
        var options = new PvwaPullOptions { CachedDetails = new Dictionary<string, PvwaAccountDetail> { ["60_6"] = Cached("60_6", rec, "Cached type") } };

        var snapshot = await Client(fake).PullAsync(Token, CancellationToken.None, options);

        Assert.DoesNotContain(fake.Requests, r => r.PathAndQuery.Contains("Keywords=CyberArkSHRec"));
        Assert.Equal(("Cached type", false), (snapshot.Accounts.Single(a => a.Id == "60_6").Detail!.DeviceType, snapshot.Accounts.Single(a => a.Id == "60_6").DetailReadThisPull));

        // No options (Test): no classic search at all.
        var test = RichPvwa();
        test.Routes[ClassicSearch] = _ => Ok(ClassicRec);
        await Client(test).PullAsync(Token, CancellationToken.None);
        Assert.DoesNotContain(test.Requests, r => r.PathAndQuery.StartsWith(ClassicSearch + "?"));
    }

    [Fact]
    public async Task ClassicDetails_ReadTheDocumentedSpelling_AndAFailureIsOneWarning()
    {
        var fake = RichPvwa();
        // The documentation spells it "Internal Properties".
        fake.Routes[ClassicSearch] = request => request.RequestUri!.Query.Contains("CyberArkSHRec")
            ? Ok("""{"Count":1,"accounts":[{"AccountID":"60_6","Internal Properties":[{"Key":"CreationMethod","Value":"AutoDetected"}],"Properties":[]}]}""")
            : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("{\"ErrorMessage\":\"Not allowed\"}") };

        var snapshot = await Client(fake).PullAsync(Token, CancellationToken.None, new PvwaPullOptions { CachedDetails = new Dictionary<string, PvwaAccountDetail>() });

        Assert.Equal("AutoDetected", snapshot.Accounts.Single(a => a.Id == "60_6").Detail!.CreationMethod);
        Assert.Null(snapshot.Accounts.Single(a => a.Id == "60_7").Detail);
        var warning = Assert.Single(snapshot.Warnings);
        Assert.StartsWith("1 account(s): device type, creation method and linked accounts not read", warning);
        Assert.Contains("60_7 (PVWA refused", warning);
    }

    private const string ApplicationsPath = "/PasswordVault/WebServices/PIMServices.svc/Applications/";

    [Fact]
    public async Task Applications_AreReadWithTheirMethods_KeepingNoHashValue()
    {
        // D-282: the shapes the lab's PVWA returned on 2026-10-21 (values made up).
        var fake = RichPvwa();
        fake.Routes[ClassicSearch] = _ => Ok("""{"Count":0,"accounts":[]}""");
        fake.Json(ApplicationsPath, """
            {"application":[
              {"AccessPermittedFrom":0,"AccessPermittedTo":24,"AllowExtendedAuthenticationRestrictions":true,"AppID":"APP_A","BusinessOwnerEmail":"o@example.com",
               "BusinessOwnerFName":"Olga","BusinessOwnerLName":"","BusinessOwnerPhone":"","Description":"Payroll","Disabled":false,"ExpirationDate":null,"Location":"\\Applications"},
              {"AppID":"APP+B","Disabled":true}]}
            """);
        fake.Json(ApplicationsPath + "APP_A/Authentications/", """
            {"authentication":[
              {"AllowInternalScripts":null,"AppID":"APP_A","AuthType":"machineAddress","AuthValue":"192.0.2.10","Comment":null,"IsFolder":null,"authID":"1"},
              {"AllowInternalScripts":false,"AppID":"APP_A","AuthType":"hash","AuthValue":"9F3C55AA","Comment":"build 7","IsFolder":null,"authID":"2"},
              {"AppID":"APP_A","AuthType":"certificateattr","Subject":"CN=app","Issuer":"CN=ca","SubjectAlternativeName":"DNS Name=app.example.com","authID":"3"}]}
            """);

        var snapshot = await Client(fake).PullAsync(Token, CancellationToken.None, new PvwaPullOptions { CachedDetails = new Dictionary<string, PvwaAccountDetail>() });

        Assert.True(snapshot.ApplicationsRead);
        var a = snapshot.Applications.Single(x => x.AppId == "APP_A");
        Assert.Equal(("Payroll", "\\Applications", (bool?)false, (string?)null, (int?)0, (int?)24, (bool?)true), (a.Description, a.Location, a.Disabled, a.ExpirationDate, a.AccessPermittedFrom, a.AccessPermittedTo, a.AllowExtendedAuthenticationRestrictions));
        Assert.Equal(("Olga", (string?)null, "o@example.com"), (a.BusinessOwnerFirstName, a.BusinessOwnerLastName, a.BusinessOwnerEmail));
        Assert.True(a.AuthenticationsRead);
        Assert.Equal([("machineAddress", "192.0.2.10"), ("hash", (string?)null), ("certificateattr", null)], a.Authentications.Select(x => (x.AuthType, x.AuthValue)));
        Assert.Equal("build 7", a.Authentications[1].Comment);
        Assert.Equal(("CN=app", "CN=ca", "DNS Name=app.example.com"), (a.Authentications[2].Subject, a.Authentications[2].Issuer, a.Authentications[2].SubjectAlternativeName));

        // An AppID with + isn't sent; the application is still listed, its methods not read.
        var b = snapshot.Applications.Single(x => x.AppId == "APP+B");
        Assert.False(b.AuthenticationsRead);
        Assert.Contains(snapshot.Warnings, w => w.StartsWith("1 CyberArk Application(s): authentication methods not read") && w.Contains("APP+B (its name has + & or %)"));
        Assert.DoesNotContain(fake.Requests, r => r.PathAndQuery.Contains("APP%2BB"));
    }

    [Fact]
    public async Task Applications_ListRefused_IsAWarning_AndNotReadByTest()
    {
        var fake = RichPvwa();
        fake.Routes[ClassicSearch] = _ => Ok("""{"Count":0,"accounts":[]}""");
        fake.Json(ApplicationsPath, "{\"ErrorMessage\":\"Not authorized\"}", HttpStatusCode.Forbidden);

        var snapshot = await Client(fake).PullAsync(Token, CancellationToken.None, new PvwaPullOptions { CachedDetails = new Dictionary<string, PvwaAccountDetail>() });

        Assert.False(snapshot.ApplicationsRead);
        Assert.Empty(snapshot.Applications);
        Assert.Contains(snapshot.Warnings, w => w.StartsWith("CyberArk Applications not read") && w.Contains("Audit Users"));

        var test = RichPvwa();
        await Client(test).PullAsync(Token, CancellationToken.None);
        Assert.DoesNotContain(test.Requests, r => r.PathAndQuery.Contains("/Applications"));
    }

    [Fact]
    public async Task TargetPlatformsRefused_IsAWarning_AndThePlatformListIsStillRead()
    {
        var fake = RichPvwa();
        fake.Json("/PasswordVault/API/Platforms/Targets", "{\"ErrorMessage\":\"Not allowed\"}", HttpStatusCode.Forbidden);

        var snapshot = await Client(fake).PullAsync(Token, CancellationToken.None);

        var platform = Assert.Single(snapshot.Platforms);
        Assert.Equal(7, platform.VerifyEveryXDays);
        Assert.Null(platform.RequireReasonForAccess);
        Assert.Contains("Target platform settings not read", Assert.Single(snapshot.Warnings));
    }

    [Fact]
    public async Task SafeMembers_AreAskedForWithPredefinedUsers()
    {
        var fake = FullPvwa();

        await Client(fake).PullAsync(Token, CancellationToken.None);

        var call = Assert.Single(fake.Requests, r => r.PathAndQuery.Contains("/Members"));
        Assert.StartsWith("/PasswordVault/API/Safes/My%20Safe/Members?filter=includePredefinedUsers%20eq%20true&limit=1000&offset=0", call.PathAndQuery);
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
    public async Task UsersListShorterThanItsTotal_IsAWarning_NotAFailure()
    {
        var fake = FullPvwa();
        fake.Json("/PasswordVault/API/Users", "{\"Users\":[{\"id\":47,\"username\":\"a\"}],\"Total\":44}");

        var snapshot = await Client(fake).PullAsync(Token, CancellationToken.None);

        Assert.Single(snapshot.Users);
        var warning = Assert.Single(snapshot.Warnings);
        Assert.Contains("PVWA reported 44 users but returned 1; 43 user(s)", warning);
    }

    [Fact]
    public async Task Users_AreReadWithThePlainList_BecauseExtendedDetailsDropsTheMasterUser()
    {
        var fake = FullPvwa();

        await Client(fake).PullAsync(Token, CancellationToken.None);

        var call = Assert.Single(fake.Requests, r => r.PathAndQuery.StartsWith("/PasswordVault/API/Users") && !r.PathAndQuery.Contains("/Users/"));
        Assert.Equal("/PasswordVault/API/Users", call.PathAndQuery);
    }

    [Fact]
    public async Task ComponentAndBuiltInUsers_AreNotAskedForDetails()
    {
        var fake = FullPvwa();
        fake.Json("/PasswordVault/API/Users",
            "{\"Users\":[{\"id\":0,\"username\":\"Master\",\"componentUser\":false,\"userType\":\"Built-InAdmins\",\"enableUser\":true,\"personalDetails\":{}},"
            + "{\"id\":22,\"username\":\"PasswordManager\",\"componentUser\":true,\"userType\":\"CPM\"},"
            + "{\"id\":47,\"username\":\"adm.jesse\",\"componentUser\":false,\"userType\":\"EPVUser\"}],\"Total\":3}");

        var snapshot = await Client(fake).PullAsync(Token, CancellationToken.None);

        Assert.Equal([0L, 22L, 47L], snapshot.Users.Select(u => u.Id));
        Assert.DoesNotContain(fake.Requests, r => r.PathAndQuery == "/PasswordVault/API/Users/22/");
        Assert.DoesNotContain(fake.Requests, r => r.PathAndQuery == "/PasswordVault/API/Users/0/");   // Master: its details call returns a 500
        Assert.Contains(fake.Requests, r => r.PathAndQuery == "/PasswordVault/API/Users/47/");
        Assert.Null(snapshot.Users.Single(u => u.Id == 22).Email);
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
