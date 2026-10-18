using System.Net;
using System.Net.Http.Json;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-245: two existing targets linked by hand (POST /api/admin/targets/{key}/link), the rules that keep a link one
/// level deep, a pair linked again after Unlink, and a target delete that no longer stops at its Target Match
/// Review rows. Every target uses a per-test token and is removed afterwards.
/// </summary>
public class TargetManualLinkTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public TargetManualLinkTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed record TypeResponse(int TargetTypeKey, string TypeCode);
    private sealed record KeyResponse(int TargetKey);
    private sealed record LinkedItem(int TargetKey);
    private sealed record TargetDetail(int TargetKey, int? LinkedToTargetKey, string? LinkedToTargetName, List<LinkedItem> LinkedTargets);

    private HttpClient Client(string user = "TestUser.Admin")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private static string NewIp() => $"10.94.{Random.Shared.Next(1, 254)}.{Random.Shared.Next(1, 254)}";

    private static async Task<int> CreateTargetAsync(HttpClient client, string name, string ip)
    {
        var types = await client.GetFromJsonAsync<List<TypeResponse>>("/api/admin/targets/target-types");
        var response = await client.PostAsJsonAsync("/api/admin/targets", new
        {
            targetTypeKey = types!.Single(t => t.TypeCode == "Server").TargetTypeKey,
            targetName = name,
            riskScore = 400,
            identifiers = new[] { new { identifierType = "IPAddress", identifierValue = ip } }
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<KeyResponse>())!.TargetKey;
    }

    private static Task<HttpResponseMessage> LinkAsync(HttpClient client, int targetKey, int primaryKey) =>
        client.PostAsJsonAsync($"/api/admin/targets/{targetKey}/link", new { primaryTargetKey = primaryKey });

    private static async Task CleanUpAsync(IEnumerable<int> targetKeys, params string[] ips)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var keys = targetKeys.ToList();
        await connection.ExecuteAsync("""
            DELETE FROM web.target_match_review WHERE IdentifierValue IN @Ips OR CandidateTargetKey IN @Keys OR ResolvedTargetKey IN @Keys;
            DELETE FROM web.target_link WHERE LinkedTargetKey IN @Keys OR PrimaryTargetKey IN @Keys;
            DELETE FROM web.target_identifier WHERE TargetKey IN @Keys;
            DELETE FROM web.dim_target WHERE TargetKey IN @Keys;
            """, new { Keys = keys.Count == 0 ? [-1] : keys, Ips = ips });
    }

    [Fact]
    public async Task ManualLink_LinksTwoExistingTargets_ShowsBothSides_AndIsAudited()
    {
        var client = Client();
        var token = $"mlk{Guid.NewGuid():N}"[..14];
        var (ipA, ipB) = (NewIp(), NewIp());
        var a = await CreateTargetAsync(client, $"{token}-a", ipA);
        var b = await CreateTargetAsync(client, $"{token}-b", ipB);
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await LinkAsync(client, a, b)).StatusCode);

            var linked = (await client.GetFromJsonAsync<TargetDetail>($"/api/admin/targets/{a}"))!;
            Assert.Equal(b, linked.LinkedToTargetKey);
            Assert.Equal($"{token}-b", linked.LinkedToTargetName);
            Assert.Equal([a], (await client.GetFromJsonAsync<TargetDetail>($"/api/admin/targets/{b}"))!.LinkedTargets.Select(t => t.TargetKey));

            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            var row = await connection.QuerySingleAsync<(string Method, int Primary, DateTime? Unlinked)>(
                "SELECT LinkMethod, PrimaryTargetKey, UnlinkedDate FROM web.target_link WHERE LinkedTargetKey = @a", new { a });
            Assert.Equal(("Manual", b, (DateTime?)null), row);
            Assert.Equal(1, await connection.QuerySingleAsync<int>("""
                SELECT COUNT(*) FROM web.audit_event e JOIN web.audit_field_change c ON c.AuditEventKey = e.AuditEventKey
                WHERE e.EntityName = 'dim_target' AND e.EntityKey = @Key AND c.FieldName = 'LinkedToTargetKey'
                  AND c.OldValue IS NULL AND c.NewValue = @Primary
                """, new { Key = a.ToString(), Primary = b.ToString() }));
        }
        finally
        {
            await CleanUpAsync([a, b], ipA, ipB);
        }
    }

    [Fact]
    public async Task ManualLink_KeepsALinkOneLevelDeep_AndNeedsManageTargets()
    {
        var client = Client();
        var token = $"mlr{Guid.NewGuid():N}"[..14];
        var (ipA, ipB, ipC) = (NewIp(), NewIp(), NewIp());
        var a = await CreateTargetAsync(client, $"{token}-a", ipA);
        var b = await CreateTargetAsync(client, $"{token}-b", ipB);
        var c = await CreateTargetAsync(client, $"{token}-c", ipC);
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await LinkAsync(client, a, b)).StatusCode);   // a is linked to b

            Assert.Equal(HttpStatusCode.BadRequest, (await LinkAsync(client, c, c)).StatusCode);          // itself
            Assert.Equal(HttpStatusCode.Conflict, (await LinkAsync(client, a, c)).StatusCode);            // a is linked already
            Assert.Equal(HttpStatusCode.Conflict, (await LinkAsync(client, b, c)).StatusCode);            // b has a linked target
            Assert.Equal(HttpStatusCode.BadRequest, (await LinkAsync(client, c, a)).StatusCode);          // the primary is itself linked
            Assert.Equal(HttpStatusCode.BadRequest, (await LinkAsync(client, c, int.MaxValue)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await LinkAsync(client, int.MaxValue, c)).StatusCode);

            var conflict = await LinkAsync(client, a, c);
            Assert.Contains("Unlink it first", await conflict.Content.ReadAsStringAsync());

            // Nothing changed: a is still linked to b, c to nothing.
            Assert.Equal(b, (await client.GetFromJsonAsync<TargetDetail>($"/api/admin/targets/{a}"))!.LinkedToTargetKey);
            Assert.Null((await client.GetFromJsonAsync<TargetDetail>($"/api/admin/targets/{c}"))!.LinkedToTargetKey);

            // The Viewer role holds no ManageTargets.
            Assert.Equal(HttpStatusCode.Forbidden, (await LinkAsync(Client("TestUser.Viewer"), c, b)).StatusCode);
        }
        finally
        {
            await CleanUpAsync([a, b, c], ipA, ipB, ipC);
        }
    }

    [Fact]
    public async Task ManualLink_LinksAnUnlinkedPairAgain_WithTheSameRow()
    {
        var client = Client();
        var token = $"mlu{Guid.NewGuid():N}"[..14];
        var (ipA, ipB, ipC) = (NewIp(), NewIp(), NewIp());
        var a = await CreateTargetAsync(client, $"{token}-a", ipA);
        var b = await CreateTargetAsync(client, $"{token}-b", ipB);
        var c = await CreateTargetAsync(client, $"{token}-c", ipC);
        try
        {
            Assert.Equal(HttpStatusCode.NoContent, (await LinkAsync(client, a, b)).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/admin/targets/{a}/unlink", null)).StatusCode);
            Assert.Null((await client.GetFromJsonAsync<TargetDetail>($"/api/admin/targets/{a}"))!.LinkedToTargetKey);

            // A by hand is allowed again (only imports never relink), to the same primary or another.
            Assert.Equal(HttpStatusCode.NoContent, (await LinkAsync(client, a, c)).StatusCode);
            Assert.Equal(c, (await client.GetFromJsonAsync<TargetDetail>($"/api/admin/targets/{a}"))!.LinkedToTargetKey);
            Assert.Empty((await client.GetFromJsonAsync<TargetDetail>($"/api/admin/targets/{b}"))!.LinkedTargets);

            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            Assert.Equal(1, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.target_link WHERE LinkedTargetKey = @a AND UnlinkedDate IS NULL AND PrimaryTargetKey = @c AND LinkMethod = 'Manual'", new { a, c }));
            Assert.Equal(1, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.target_link WHERE LinkedTargetKey = @a", new { a }));   // one row per target
        }
        finally
        {
            await CleanUpAsync([a, b, c], ipA, ipB, ipC);
        }
    }

    [Fact]
    public async Task DeletingATarget_DropsItsPendingReviewRows_AndKeepsResolvedOnesWithoutIt()
    {
        var client = Client();
        var token = $"mld{Guid.NewGuid():N}"[..14];
        var (ip, resolvedIp) = (NewIp(), NewIp());
        var target = await CreateTargetAsync(client, $"{token}-t", ip);
        var keys = new List<int> { target };
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        try
        {
            var pending = await connection.QuerySingleAsync<int>("""
                INSERT INTO web.target_match_review (CandidateTargetKey, IdentifierType, IdentifierValue, TargetName, DiscoverySource)
                OUTPUT inserted.TargetMatchReviewKey VALUES (@target, 'IPAddress', @ip, @name, 'CMDB')
                """, new { target, ip, name = $"{token}-incoming" });
            await connection.ExecuteAsync("INSERT INTO web.target_match_review_identifier (TargetMatchReviewKey, IdentifierType, IdentifierValue) VALUES (@pending, 'Hostname', @host)",
                new { pending, host = $"{token}.example.com" });
            var resolved = await connection.QuerySingleAsync<int>("""
                INSERT INTO web.target_match_review (CandidateTargetKey, IdentifierType, IdentifierValue, TargetName, DiscoverySource, Resolution, ResolvedDate)
                OUTPUT inserted.TargetMatchReviewKey VALUES (@target, 'IPAddress', @resolvedIp, @name, 'CMDB', 'Ignored', SYSUTCDATETIME())
                """, new { target, resolvedIp, name = $"{token}-ignored" });

            Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/admin/targets/{target}")).StatusCode);

            Assert.Equal(0, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.target_match_review WHERE TargetMatchReviewKey = @pending", new { pending }));
            Assert.Equal(0, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.target_match_review_identifier WHERE TargetMatchReviewKey = @pending", new { pending }));
            var kept = await connection.QuerySingleAsync<(int? Candidate, string Resolution)>("SELECT CandidateTargetKey, Resolution FROM web.target_match_review WHERE TargetMatchReviewKey = @resolved", new { resolved });
            Assert.Equal(((int?)null, "Ignored"), kept);   // history and the Ignore stay, naming no target
            keys.Clear();
        }
        finally
        {
            await CleanUpAsync(keys, ip, resolvedIp);
            await connection.ExecuteAsync("DELETE FROM web.target_match_review WHERE IdentifierValue IN @ips", new { ips = new[] { ip, resolvedIp } });
        }
    }
}
