using System.Net;
using System.Net.Http.Json;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-232: Resolve, Undo and the user/group search on Reports > Unresolved
/// Entitlement Members (ConfirmReconciliation), the report's Show resolved
/// filter (ViewDashboard) and the EntitlementMemberResolution audit events.
/// Each test adds its own Privilege Cloud safe, user, group and unresolved
/// entitlements, named with a per-test token, and removes them afterwards.
/// </summary>
public class EntitlementMemberResolutionTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private const string Base = "/api/reports/unresolved-entitlement-members";
    private readonly BlueTrackWebApplicationFactory _factory;

    public EntitlementMemberResolutionTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private sealed record Seed(string Token, int UserKey, int GroupKey, string UserMemberId, string GroupMemberId, string UserName, string GroupName);

    private sealed record ReportRow(string SafeName, string MemberType, string UnresolvedMemberId, string? MemberName, string? ResolutionMethod,
        string? ResolvedToName, string? ResolutionNote, string? ResolvedByName);

    private sealed record Principal(int Key, string Name, string? Detail);

    /// <summary>
    /// A Privilege Cloud safe with two unresolved entitlements: a user whose
    /// name is a known user's (as an entitlement loaded before D-232 would be)
    /// and a role-style group no group has.
    /// </summary>
    private static async Task<Seed> SeedAsync()
    {
        var token = $"emr{Guid.NewGuid():N}"[..14];
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var keys = await connection.QuerySingleAsync<(int UserKey, int GroupKey)>("""
            DECLARE @src INT = (SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'PRIVCLOUD');
            INSERT INTO dim_safe (SourceSystemKey, SafeUrlId, SafeName) VALUES (@src, @token + '_Safe', @token + '_Safe');
            DECLARE @safeKey INT = SCOPE_IDENTITY();
            INSERT INTO dim_user (SourceSystemKey, SourceUserId, Username, Email) VALUES (@src, @token + '-U1', @token + '.target', @token + '@example.test');
            DECLARE @userKey INT = SCOPE_IDENTITY();
            INSERT INTO dim_group (SourceSystemKey, SourceGroupId, GroupName) VALUES (@src, @token + '-G1', @token + ' Vault Admins');
            DECLARE @groupKey INT = SCOPE_IDENTITY();
            INSERT INTO fact_safe_entitlement (SafeKey, MemberType, UnresolvedMemberId, MemberName, SnapshotDate)
            VALUES (@safeKey, 'User', @token + '-guid', @token + '.target', CAST(SYSUTCDATETIME() AS DATE)),
                   (@safeKey, 'Group', @token + '_Role', @token + '_Role', CAST(SYSUTCDATETIME() AS DATE));
            SELECT @userKey, @groupKey;
            """, new { token });
        return new Seed(token, keys.UserKey, keys.GroupKey, $"{token}-guid", $"{token}_Role", $"{token}.target", $"{token} Vault Admins");
    }

    private static async Task CleanUpAsync(Seed seed)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            DELETE FROM web.entitlement_member_resolution WHERE SourceMemberId LIKE @token + '%';
            DELETE fse FROM fact_safe_entitlement fse JOIN dim_safe sf ON sf.SafeKey = fse.SafeKey WHERE sf.SafeUrlId = @token + '_Safe';
            DELETE FROM dim_safe WHERE SafeUrlId = @token + '_Safe';
            DELETE FROM dim_user WHERE SourceUserId LIKE @token + '%';
            DELETE FROM dim_group WHERE SourceGroupId LIKE @token + '%';
            """, new { seed.Token });
    }

    private static async Task<List<ReportRow>> ReportAsync(HttpClient client, Seed seed, bool showResolved)
    {
        var rows = await client.GetFromJsonAsync<List<ReportRow>>($"{Base}?showResolved={(showResolved ? "true" : "false")}");
        return rows!.Where(r => r.SafeName == $"{seed.Token}_Safe").OrderBy(r => r.MemberType).ToList();
    }

    private static async Task<int> AuditCountAsync(Seed seed)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<int>("""
            SELECT COUNT(*) FROM web.audit_event e
            JOIN web.dim_audit_event_type t ON t.AuditEventTypeKey = e.AuditEventTypeKey AND t.EventTypeName = 'EntitlementMemberResolution'
            WHERE e.EntityName = 'entitlement_member_resolution' AND e.EntityKey LIKE @token + '%'
            """, new { seed.Token });
    }

    [Theory]
    [InlineData("TestUser.Viewer")]
    [InlineData("TestUser.Analyst")]
    public async Task WithoutConfirmReconciliation_TheReportLoads_ButResolveUndoAndSearchAreForbidden(string user)
    {
        var seed = await SeedAsync();
        try
        {
            var client = Client(user);
            Assert.Equal(2, (await ReportAsync(client, seed, showResolved: false)).Count);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"{Base}/principals?memberType=User&q={seed.Token}")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Base}/resolve",
                new { memberType = "Group", sourceMemberId = seed.GroupMemberId, resolution = "External" })).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{Base}/unresolve",
                new { memberType = "Group", sourceMemberId = seed.GroupMemberId })).StatusCode);
            Assert.Equal(0, await AuditCountAsync(seed));
        }
        finally
        {
            await CleanUpAsync(seed);
        }
    }

    [Fact]
    public async Task Search_FindsPrivilegeCloudUsersOrGroupsOfTheMembersType_WithLikeCharactersEscaped()
    {
        var seed = await SeedAsync();
        try
        {
            var client = Client("TestUser.Approver");
            var users = (await client.GetFromJsonAsync<List<Principal>>($"{Base}/principals?memberType=User&q={seed.Token}"))!;
            var user = Assert.Single(users);
            Assert.Equal((seed.UserKey, seed.UserName, $"{seed.Token}@example.test"), (user.Key, user.Name, user.Detail));

            var groups = (await client.GetFromJsonAsync<List<Principal>>($"{Base}/principals?memberType=Group&q={Uri.EscapeDataString(seed.Token + " vault")}"))!;
            Assert.Equal(seed.GroupKey, Assert.Single(groups).Key);

            // '_' and '%' are literal, not wildcards.
            Assert.Empty((await client.GetFromJsonAsync<List<Principal>>($"{Base}/principals?memberType=User&q={Uri.EscapeDataString(seed.Token[..6] + "_%")}"))!);

            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Base}/principals?memberType=User&q=a")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync($"{Base}/principals?memberType=Role&q={seed.Token}")).StatusCode);
        }
        finally
        {
            await CleanUpAsync(seed);
        }
    }

    [Fact]
    public async Task Resolve_LinksOrLabels_TheMemberLeavesTheReport_ShowResolvedSaysHow_AndUndoRestoresIt()
    {
        var seed = await SeedAsync();
        try
        {
            var client = Client("TestUser.Approver");
            var before = await ReportAsync(client, seed, showResolved: false);
            Assert.Equal([seed.GroupMemberId, seed.UserMemberId], before.Select(r => r.UnresolvedMemberId).ToArray());
            Assert.Equal(seed.UserName, before[1].MemberName);

            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"{Base}/resolve",
                new { memberType = "User", sourceMemberId = seed.UserMemberId, resolution = "Linked", userKey = seed.UserKey })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"{Base}/resolve",
                new { memberType = "Group", sourceMemberId = seed.GroupMemberId, resolution = "External", note = "  Built-in cloud role  " })).StatusCode);

            Assert.Empty(await ReportAsync(client, seed, showResolved: false));
            var resolved = await ReportAsync(client, seed, showResolved: true);
            Assert.Equal(("External", (string?)null, "Built-in cloud role"), (resolved[0].ResolutionMethod, resolved[0].ResolvedToName, resolved[0].ResolutionNote));
            Assert.Equal(("Manual", seed.UserName), (resolved[1].ResolutionMethod, resolved[1].ResolvedToName));
            Assert.All(resolved, r => Assert.False(string.IsNullOrEmpty(r.ResolvedByName)));
            Assert.Equal(2, await AuditCountAsync(seed));

            // Undo: the user falls back to its unique name match; the role is unresolved again.
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"{Base}/unresolve",
                new { memberType = "User", sourceMemberId = seed.UserMemberId })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync($"{Base}/unresolve",
                new { memberType = "Group", sourceMemberId = seed.GroupMemberId })).StatusCode);
            var after = await ReportAsync(client, seed, showResolved: true);
            Assert.Equal(("ByName", seed.UserName, (string?)null), (after[1].ResolutionMethod, after[1].ResolvedToName, after[1].ResolvedByName));
            Assert.Null(after[0].ResolutionMethod);
            Assert.Equal([seed.GroupMemberId], (await ReportAsync(client, seed, showResolved: false)).Select(r => r.UnresolvedMemberId).ToArray());
            Assert.Equal(4, await AuditCountAsync(seed));

            // Nothing left to undo.
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync($"{Base}/unresolve",
                new { memberType = "Group", sourceMemberId = seed.GroupMemberId })).StatusCode);

            // The export follows the filter and says how each was resolved.
            var csv = ReportExportTests.ParseCsv(await (await client.GetAsync($"{Base}/export?format=csv&showResolved=true")).Content.ReadAsByteArrayAsync());
            var userLine = Assert.Single(csv, r => r.Count > 3 && r[3] == seed.UserMemberId);
            Assert.Equal([$"{seed.Token}_Safe", "User", seed.UserName, seed.UserMemberId, $"Matched by name: {seed.UserName}"], userLine.Take(5).ToArray());
        }
        finally
        {
            await CleanUpAsync(seed);
        }
    }

    [Fact]
    public async Task Resolve_RejectsBadRequests_AndAnUnknownMember()
    {
        var seed = await SeedAsync();
        try
        {
            var client = Client("TestUser.Approver");
            async Task<HttpStatusCode> PostAsync(object body) => (await client.PostAsJsonAsync($"{Base}/resolve", body)).StatusCode;

            Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(new { memberType = "Role", sourceMemberId = seed.UserMemberId, resolution = "External" }));
            Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(new { memberType = "User", sourceMemberId = "", resolution = "External" }));
            Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(new { memberType = "User", sourceMemberId = seed.UserMemberId, resolution = "Merged" }));
            // Linked needs a key of the member's own type, of a Privilege Cloud user or group.
            Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(new { memberType = "Group", sourceMemberId = seed.GroupMemberId, resolution = "Linked", userKey = seed.UserKey }));
            Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(new { memberType = "User", sourceMemberId = seed.UserMemberId, resolution = "Linked", userKey = int.MaxValue }));
            Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(new { memberType = "Group", sourceMemberId = seed.GroupMemberId, resolution = "External", note = new string('x', 501) }));
            Assert.Equal(HttpStatusCode.NotFound, await PostAsync(new { memberType = "User", sourceMemberId = seed.Token + "-nobody", resolution = "External" }));
            Assert.Equal(0, await AuditCountAsync(seed));
            Assert.Equal(2, (await ReportAsync(client, seed, showResolved: false)).Count);
        }
        finally
        {
            await CleanUpAsync(seed);
        }
    }
}
