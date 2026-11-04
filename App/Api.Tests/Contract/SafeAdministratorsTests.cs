using System.Net;
using System.Net.Http.Json;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-335: GET /api/reports/safe-administrators and its export. Each test adds a Self-Hosted Safe named with a per-test
/// token, with: a user holding every permission; a user with Use only (not listed); a group with Manage Safe Members
/// whose AD user was read through a nested group; CyberArk's own Master (built-in, hidden by default); and a built-in Vault
/// group (Vault Admins) whose person is shown (the user's choice: built-in groups shown, built-in users hidden).
/// Removed afterwards.
/// </summary>
public class SafeAdministratorsTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public SafeAdministratorsTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private static Task SeedAsync(SqlConnection connection, string token) => connection.ExecuteAsync("""
        DECLARE @sh INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'SELFHOSTED');
        DECLARE @today DATE = CAST(SYSUTCDATETIME() AS DATE);
        INSERT INTO dim_safe (SourceSystemKey, SafeUrlId, SafeName) VALUES (@sh, @token + '_Safe', @token + '_Safe');
        DECLARE @safe INT = SCOPE_IDENTITY();
        INSERT INTO dim_user (SourceSystemKey, SourceUserId, Username, FirstName, LastName, UserType) VALUES (@sh, @token + '-F', @token + '.full', 'Fay', 'Lee', '34');
        DECLARE @full INT = SCOPE_IDENTITY();
        INSERT INTO dim_user (SourceSystemKey, SourceUserId, Username, UserType) VALUES (@sh, @token + '-U', @token + '.user', '34');
        DECLARE @user INT = SCOPE_IDENTITY();
        INSERT INTO dim_user (SourceSystemKey, SourceUserId, Username, UserType) VALUES (@sh, @token + '-M', @token + '.Master', '10');
        DECLARE @master INT = SCOPE_IDENTITY();
        INSERT INTO dim_group (SourceSystemKey, SourceGroupId, GroupName) VALUES (@sh, @token + '-G', @token + ' Safe Managers');
        DECLARE @group INT = SCOPE_IDENTITY();
        INSERT INTO dim_user (SourceSystemKey, SourceUserId, Username, UserType) VALUES (@sh, @token + '-V', @token + '.vaultadmin', '34');
        DECLARE @vaultAdmin INT = SCOPE_IDENTITY();
        INSERT INTO dim_group (SourceSystemKey, SourceGroupId, GroupName) VALUES (@sh, @token + '-VA', @token + ' Vault Admins');
        DECLARE @vaultAdmins INT = SCOPE_IDENTITY();
        INSERT INTO bridge_group_membership (GroupKey, UserKey, SnapshotDate) VALUES (@vaultAdmins, @vaultAdmin, @today);
        INSERT INTO fact_safe_entitlement (SafeKey, MemberType, GroupKey, IsPredefinedUser, SnapshotDate) VALUES (@safe, 'Group', @vaultAdmins, 1, @today);
        INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted)
        SELECT SCOPE_IDENTITY(), PermissionKey, 1 FROM dim_permission WHERE PermissionName IN @fullRights;

        INSERT INTO fact_safe_entitlement (SafeKey, MemberType, UserKey, SnapshotDate) VALUES (@safe, 'User', @full, @today);
        INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted)
        SELECT SCOPE_IDENTITY(), PermissionKey, 1 FROM dim_permission WHERE PermissionName IN @fullRights;
        INSERT INTO fact_safe_entitlement (SafeKey, MemberType, UserKey, SnapshotDate) VALUES (@safe, 'User', @user, @today);
        INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted)
        SELECT SCOPE_IDENTITY(), PermissionKey, 1 FROM dim_permission WHERE PermissionName = 'UseAccounts';
        INSERT INTO fact_safe_entitlement (SafeKey, MemberType, UserKey, IsPredefinedUser, SnapshotDate) VALUES (@safe, 'User', @master, 1, @today);
        INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted)
        SELECT SCOPE_IDENTITY(), PermissionKey, 1 FROM dim_permission WHERE PermissionName IN @fullRights;
        INSERT INTO fact_safe_entitlement (SafeKey, MemberType, GroupKey, SnapshotDate) VALUES (@safe, 'Group', @group, @today);
        INSERT INTO bridge_entitlement_permission (EntitlementKey, PermissionKey, IsGranted)
        SELECT SCOPE_IDENTITY(), PermissionKey, CASE WHEN PermissionName = 'ManageSafe' THEN 0 ELSE 1 END
        FROM dim_permission WHERE PermissionName IN ('ManageSafe', 'ManageSafeMembers', 'ListAccounts');
        INSERT INTO ad_group_member (GroupKey, DomainName, SamAccountName, DisplayName, IsEnabled, NestedPath) VALUES (@group, 'company.com', @token + 'ann', 'Ann Lee', 1, 'Team A');
        INSERT INTO ad_group_read (GroupKey, MatchedBy, Status, DomainName, MemberCount, LastAttemptAt, LastFoundAt) VALUES (@group, 'Dn', 'Found', 'company.com', 1, SYSUTCDATETIME(), SYSUTCDATETIME());
        """, new { token, fullRights = SafeAdministratorsRepository.FullRightsPermissions });

    private static Task CleanUpAsync(SqlConnection connection, string token) => connection.ExecuteAsync("""
        DELETE m FROM ad_group_member m JOIN dim_group g ON g.GroupKey = m.GroupKey WHERE g.SourceGroupId = @token + '-G';
        DELETE r FROM ad_group_read r JOIN dim_group g ON g.GroupKey = r.GroupKey WHERE g.SourceGroupId = @token + '-G';
        DELETE b FROM bridge_entitlement_permission b JOIN fact_safe_entitlement f ON f.EntitlementKey = b.EntitlementKey JOIN dim_safe s ON s.SafeKey = f.SafeKey WHERE s.SafeUrlId = @token + '_Safe';
        DELETE f FROM fact_safe_entitlement f JOIN dim_safe s ON s.SafeKey = f.SafeKey WHERE s.SafeUrlId = @token + '_Safe';
        DELETE FROM dim_safe WHERE SafeUrlId = @token + '_Safe';
        DELETE b FROM bridge_group_membership b JOIN dim_group g ON g.GroupKey = b.GroupKey WHERE g.SourceGroupId LIKE @token + '-%';
        DELETE FROM dim_user WHERE SourceUserId LIKE @token + '-%';
        DELETE FROM dim_group WHERE SourceGroupId LIKE @token + '-%';
        """, new { token });

    [Fact]
    public async Task ListsWhoManagesEachSafe_WithFullRights_HowTheyReachIt_AndWhetherBuiltIn()
    {
        var token = $"sad{Guid.NewGuid():N}"[..12];
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await SeedAsync(connection, token);
        try
        {
            var rows = (await Client("TestUser.Viewer").GetFromJsonAsync<List<SafeAdministratorRow>>("/api/reports/safe-administrators"))!
                .Where(r => r.SafeName == token + "_Safe").ToList();

            Assert.Equal([$"company.com\\{token}ann", $"{token}.Master", $"{token}.full", $"{token}.vaultadmin"], rows.Select(r => r.PersonName).Order(StringComparer.Ordinal));
            var full = rows.Single(r => r.PersonName == $"{token}.full");
            Assert.Equal((true, true, true, false, "Direct", "Fay Lee"), (full.ManageSafe, full.ManageSafeMembers, full.FullRights, full.IsBuiltIn, full.AccessPaths, full.DisplayName));
            var ann = rows.Single(r => r.PersonName.EndsWith("ann"));
            Assert.Equal((false, true, false, false), (ann.ManageSafe, ann.ManageSafeMembers, ann.FullRights, ann.IsBuiltIn));
            Assert.Equal($"Via AD group: {token} Safe Managers > Team A", ann.AccessPaths);
            Assert.True(rows.Single(r => r.PersonName == $"{token}.Master").IsBuiltIn);
            var vaultAdmin = rows.Single(r => r.PersonName == $"{token}.vaultadmin");
            Assert.Equal((false, true, $"Via Vault group: {token} Vault Admins"), (vaultAdmin.IsBuiltIn, vaultAdmin.FullRights, vaultAdmin.AccessPaths));
        }
        finally
        {
            await CleanUpAsync(connection, token);
        }
    }

    [Fact]
    public async Task Export_LeavesOutBuiltIn_UnlessAsked_AndAddsUpByPerson()
    {
        var token = $"sae{Guid.NewGuid():N}"[..12];
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await SeedAsync(connection, token);
        try
        {
            var client = Client("TestUser.Viewer");
            var bySafe = await client.GetStringAsync("/api/reports/safe-administrators/export?format=csv");
            Assert.StartsWith("Safe,Source,Person,Name,Enabled,Manage Safe,Manage Safe Members,Full rights,How,Built-in", bySafe.TrimStart('﻿'));
            Assert.Contains($"{token}.full", bySafe);
            Assert.DoesNotContain($"{token}.Master", bySafe);
            Assert.Contains($"{token}.Master", await client.GetStringAsync("/api/reports/safe-administrators/export?format=csv&includeBuiltIn=true"));

            var byPerson = await client.GetStringAsync("/api/reports/safe-administrators/export?format=csv&view=person");
            Assert.StartsWith("Person,Name,Enabled,Safes,Manage Safe,Manage Safe Members,Full rights,Built-in", byPerson.TrimStart('﻿'));
            Assert.Contains($"{token}.full,Fay Lee,Yes,1,1,1,1,No", byPerson);
        }
        finally
        {
            await CleanUpAsync(connection, token);
        }
    }

    [Fact]
    public async Task NeedsASignedInUserWithViewDashboard()
    {
        var response = await Client("TestUser.DoesNotExist").GetAsync("/api/reports/safe-administrators");
        Assert.True(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden);
    }

    [Fact]
    public void ByPerson_CountsEachPersonsSafes_MostFullRightsFirst()
    {
        static SafeAdministratorRow Row(int safe, string person, bool full) => new()
        {
            SafeKey = safe, SafeName = $"S{safe}", SourceSystemName = "Self-Hosted", PersonName = person, AccessPaths = "Direct",
            ManageSafe = true, ManageSafeMembers = !full, FullRights = full, IsEnabled = true
        };
        var people = SafeAdministratorsRepository.ByPerson([Row(1, "ann", false), Row(1, "bob", true), Row(2, "ANN", true), Row(3, "bob", true)]);

        Assert.Equal(["bob", "ann"], people.Select(p => p.PersonName));
        Assert.Equal((2, 2, 0, 2), (people[0].Safes, people[0].ManageSafeSafes, people[0].ManageSafeMembersSafes, people[0].FullRightsSafes));
        Assert.Equal((2, 1, 1), (people[1].Safes, people[1].ManageSafeMembersSafes, people[1].FullRightsSafes));
    }
}
