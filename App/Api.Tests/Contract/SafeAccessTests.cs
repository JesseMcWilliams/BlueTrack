using System.Net;
using System.Net.Http.Json;
using BlueTrack.Api.Models;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-281/D-282: GET /api/account-progress/{key}/safe-access and GET /api/reports/cyberark-applications. Each test adds a
/// Self-Hosted Safe with an account, a user member, a group member whose AD users were read, and an application's
/// Vault user with Retrieve, named with a per-test token, and removes them afterwards.
/// </summary>
public class SafeAccessTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public SafeAccessTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private static async Task<long> SeedAsync(SqlConnection connection, string token) => await connection.QuerySingleAsync<long>("""
        DECLARE @sh INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');
        DECLARE @use INT = (SELECT PermissionKey FROM dim_permission WHERE PermissionName = 'UseAccounts');
        DECLARE @retrieve INT = (SELECT PermissionKey FROM dim_permission WHERE PermissionName = 'RetrieveAccounts');
        DECLARE @today DATE = CAST(SYSUTCDATETIME() AS DATE);
        INSERT INTO dim_safe (SourceSystemKey, SafeUrlId, SafeName) VALUES (@sh, @token + '_Safe', @token + '_Safe');
        DECLARE @safe INT = SCOPE_IDENTITY();
        INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, SafeKey, IsDeleted, IsDeletedInSource) VALUES (@sh, @token + '_1', @token + '-acct', @safe, 0, 0);
        DECLARE @account BIGINT = SCOPE_IDENTITY();
        INSERT INTO dim_user (SourceSystemKey, SourceUserId, Username, FirstName, LastName) VALUES (@sh, @token + '-U', @token + '.admin', 'Pat', 'Lee');
        DECLARE @user INT = SCOPE_IDENTITY();
        INSERT INTO dim_user (SourceSystemKey, SourceUserId, Username, UserType) VALUES (@sh, @token + '-A', @token + '_APP', '35');
        DECLARE @appUser INT = SCOPE_IDENTITY();
        INSERT INTO dim_group (SourceSystemKey, SourceGroupId, GroupName) VALUES (@sh, @token + '-G', @token + ' AD Admins');
        DECLARE @group INT = SCOPE_IDENTITY();
        INSERT INTO fact_safe_entitlement (SafeKey, MemberType, UserKey, SnapshotDate) VALUES (@safe, 'User', @user, @today);
        INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted) VALUES (SCOPE_IDENTITY(), @use, 1);
        INSERT INTO fact_safe_entitlement (SafeKey, MemberType, UserKey, SnapshotDate) VALUES (@safe, 'User', @appUser, @today);
        INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted) VALUES (SCOPE_IDENTITY(), @retrieve, 1);
        INSERT INTO fact_safe_entitlement (SafeKey, MemberType, GroupKey, SnapshotDate) VALUES (@safe, 'Group', @group, @today);
        INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted) VALUES (SCOPE_IDENTITY(), @retrieve, 1);
        INSERT INTO ad_group_member (GroupKey, DomainName, SamAccountName, DisplayName, IsEnabled) VALUES (@group, 'company.com', 'ann', 'Ann Lee', 1);
        INSERT INTO ad_group_read (GroupKey, MatchedBy, Status, DomainName, MemberCount, LastAttemptAt, LastFoundAt) VALUES (@group, 'Dn', 'Found', 'company.com', 1, '2026-10-21T02:00:00', '2026-10-21T02:00:00');
        INSERT INTO dim_cyberark_application (SourceSystemKey, AppId, Description) VALUES (@sh, @token + '_APP', 'Payroll');
        INSERT INTO dim_cyberark_application_authentication (CyberArkApplicationKey, AuthId, AuthType, AuthValue) VALUES (SCOPE_IDENTITY(), '1', 'machineAddress', '192.0.2.10');
        SELECT @account;
        """, new { token });

    private static async Task CleanUpAsync(SqlConnection connection, string token) => await connection.ExecuteAsync("""
        DELETE x FROM dim_cyberark_application_authentication x JOIN dim_cyberark_application a ON a.CyberArkApplicationKey = x.CyberArkApplicationKey WHERE a.AppId = @token + '_APP';
        DELETE FROM dim_cyberark_application WHERE AppId = @token + '_APP';
        DELETE m FROM ad_group_member m JOIN dim_group g ON g.GroupKey = m.GroupKey WHERE g.SourceGroupId = @token + '-G';
        DELETE r FROM ad_group_read r JOIN dim_group g ON g.GroupKey = r.GroupKey WHERE g.SourceGroupId = @token + '-G';
        DELETE b FROM bridge_entitlement_permission b JOIN fact_safe_entitlement f ON f.EntitlementKey = b.EntitlementKey JOIN dim_safe s ON s.SafeKey = f.SafeKey WHERE s.SafeUrlId = @token + '_Safe';
        DELETE f FROM fact_safe_entitlement f JOIN dim_safe s ON s.SafeKey = f.SafeKey WHERE s.SafeUrlId = @token + '_Safe';
        DELETE FROM fact_account WHERE SourceAccountId = @token + '_1';
        DELETE FROM dim_safe WHERE SafeUrlId = @token + '_Safe';
        DELETE FROM dim_user WHERE SourceUserId LIKE @token + '-%';
        DELETE FROM dim_group WHERE SourceGroupId = @token + '-G';
        """, new { token });

    [Fact]
    public async Task SafeAccess_ListsPeopleByPath_WithTheirPermissions_AndTheApplicationsThatCanRetrieve()
    {
        var token = $"sa{Guid.NewGuid():N}"[..12];
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var accountKey = await SeedAsync(connection, token);
        try
        {
            var access = (await Client("TestUser.Viewer").GetFromJsonAsync<AccountSafeAccess>($"/api/account-progress/{accountKey}/safe-access"))!;

            Assert.Equal([($"{token}.admin", "Direct", true, false), ("company.com\\ann", $"Via AD group: {token} AD Admins", false, true)],
                access.People.Select(p => (p.PersonName, p.AccessPath, p.CanUse, p.CanRetrieve)));
            Assert.Equal("Pat Lee", access.People[0].DisplayName);
            // The application's own Vault user is listed as an application, not a person.
            var app = Assert.Single(access.Applications);
            Assert.Equal(($"{token}_APP", "Payroll", "Direct"), (app.AppId, app.Description, app.AccessPath));
            Assert.Equal(new DateTime(2026, 10, 21, 2, 0, 0), access.AdReadAt);

            Assert.Equal(HttpStatusCode.NotFound, (await Client("TestUser.Viewer").GetAsync("/api/account-progress/999999999/safe-access")).StatusCode);
        }
        finally
        {
            await CleanUpAsync(connection, token);
        }
    }

    [Fact]
    public async Task CyberArkApplications_ListsEachWithItsMethods_AndWhatItCanRetrieve()
    {
        var token = $"ca{Guid.NewGuid():N}"[..12];
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await SeedAsync(connection, token);
        try
        {
            var apps = (await Client("TestUser.Viewer").GetFromJsonAsync<List<CyberArkApplicationSummary>>("/api/reports/cyberark-applications"))!;

            var app = Assert.Single(apps, a => a.AppId == $"{token}_APP");
            Assert.Equal(("Payroll", 1, 1, false), (app.Description, app.AccountCount, app.SafeCount, app.IsDeletedInSource));
            Assert.Equal([("machineAddress", (string?)"192.0.2.10")], app.Authentications.Select(a => (a.AuthType, a.AuthValue)));
            Assert.Equal(HttpStatusCode.Forbidden, (await Client("TestUser.DoesNotExist").GetAsync("/api/reports/cyberark-applications")).StatusCode);
        }
        finally
        {
            await CleanUpAsync(connection, token);
        }
    }
}
