using System.Net;
using System.Net.Http.Json;
using System.Text;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-225: GET /api/admin/targets/search, the typeahead behind Target Match
/// Review's link target, and linking a review row to a chosen target (D-229).
/// Every target is named with a per-test token and removed afterwards.
/// </summary>
public class TargetSearchTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public TargetSearchTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private static string NewToken() => $"srch{Guid.NewGuid():N}"[..16];

    private static async Task<int> CreateTargetAsync(HttpClient client, string name, params (string Type, string Value)[] identifiers)
    {
        var types = await client.GetFromJsonAsync<List<TargetTypeResponse>>("/api/admin/targets/target-types");
        var response = await client.PostAsJsonAsync("/api/admin/targets", new
        {
            targetTypeKey = types!.Single(t => t.TypeCode == "Server").TargetTypeKey,
            targetName = name,
            riskScore = 100,
            identifiers = identifiers.Select(i => new { identifierType = i.Type, identifierValue = i.Value }).ToArray()
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TargetKeyResponse>())!.TargetKey;
    }

    /// <summary>Removes the targets and any review rows pointing at them (a review's candidate FK blocks the delete).</summary>
    private static async Task CleanUpAsync(IEnumerable<int> targetKeys)
    {
        var keys = targetKeys.ToList();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("""
            DELETE FROM web.target_match_review WHERE CandidateTargetKey IN @Keys OR ResolvedTargetKey IN @Keys;
            DELETE FROM web.target_link WHERE LinkedTargetKey IN @Keys OR PrimaryTargetKey IN @Keys;
            DELETE FROM web.target_identifier WHERE TargetKey IN @Keys;
            DELETE FROM web.dim_target WHERE TargetKey IN @Keys;
            """, new { Keys = keys });
    }

    private static async Task<List<SearchResult>> SearchAsync(HttpClient client, string q)
    {
        var response = await client.GetAsync($"/api/admin/targets/search?q={Uri.EscapeDataString(q)}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<List<SearchResult>>())!;
    }

    [Fact]
    public async Task Search_MatchesByName_AndByIdentifierValue_WithTypeAndIdentifiers()
    {
        var client = Client("TestUser.Analyst");
        var token = NewToken();
        var byName = await CreateTargetAsync(client, $"{token}-web01", ("Hostname", $"{token}-h1"));
        var byIdentifier = await CreateTargetAsync(client, $"Other {Guid.NewGuid():N}", ("FQDN", $"db.{token}.example.test"), ("Hostname", $"x{Guid.NewGuid():N}"));
        try
        {
            var nameHit = Assert.Single(await SearchAsync(client, $"{token}-web"));
            Assert.Equal(byName, nameHit.TargetKey);
            Assert.Equal("Server", nameHit.TargetTypeDisplayName);
            Assert.Contains(nameHit.Identifiers, i => i.IdentifierType == "Hostname");

            var identifierHit = Assert.Single(await SearchAsync(client, $"db.{token}"));
            Assert.Equal(byIdentifier, identifierHit.TargetKey);
            // The matching identifier is listed first.
            Assert.Equal("FQDN", identifierHit.Identifiers[0].IdentifierType);

            // Both match the token; names starting with it come first.
            var both = await SearchAsync(client, token.ToUpperInvariant());
            Assert.Equal([byName, byIdentifier], both.Select(r => r.TargetKey).ToArray());
        }
        finally
        {
            await CleanUpAsync([byName, byIdentifier]);
        }
    }

    [Fact]
    public async Task Search_ReturnsAtMost20()
    {
        var client = Client("TestUser.Admin");
        var token = NewToken();
        var keys = new List<int>();
        try
        {
            for (var i = 0; i < 21; i++)
            {
                keys.Add(await CreateTargetAsync(client, $"{token}-{i:00}"));
            }

            var results = await SearchAsync(client, token);
            Assert.Equal(20, results.Count);
        }
        finally
        {
            await CleanUpAsync(keys);
        }
    }

    [Fact]
    public async Task Search_TreatsLikeWildcardsAsLiteralText()
    {
        var client = Client("TestUser.Admin");
        var token = NewToken();
        var special = await CreateTargetAsync(client, $"{token}_a%b[c]");
        var plain = await CreateTargetAsync(client, $"{token}Za9bc");
        try
        {
            // Unescaped, _ and % and [c] would also match the plain name.
            foreach (var q in new[] { $"{token}_a", $"{token}_a%b", "a%b[c]", "b[c]" })
            {
                var keys = (await SearchAsync(client, q)).Select(r => r.TargetKey).ToList();
                Assert.Contains(special, keys);
                Assert.DoesNotContain(plain, keys);
            }
        }
        finally
        {
            await CleanUpAsync([special, plain]);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("  b  ")]
    public async Task Search_ShorterThanTwoCharacters_Is400(string q)
    {
        var response = await Client("TestUser.Admin").GetAsync($"/api/admin/targets/search?q={Uri.EscapeDataString(q)}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Search_WithoutQuery_Is400()
    {
        var response = await Client("TestUser.Admin").GetAsync("/api/admin/targets/search");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("TestUser.Viewer")]
    [InlineData("TestUser.Approver")]
    public async Task Search_WithoutManageTargets_Is403(string user)
    {
        var response = await Client(user).GetAsync("/api/admin/targets/search?q=server");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Review_LinkedToChosenTarget_KeepsTheIdentifierOnTheCandidate()
    {
        var client = Client("TestUser.Admin");
        var ip = $"10.98.{Random.Shared.Next(1, 254)}.{Random.Shared.Next(1, 254)}";
        var candidate = await CreateTargetAsync(client, $"MergeCandidate {Guid.NewGuid():N}", ("IPAddress", ip));
        var chosen = await CreateTargetAsync(client, $"MergeChosen {Guid.NewGuid():N}");
        var keys = new List<int> { candidate, chosen };
        try
        {
            var reviewKey = await QueueReviewAsync(client, ip);

            // D-229 (replacing D-225's move): the incoming row becomes a target linked to the chosen one.
            var resolve = await client.PostAsJsonAsync($"/api/admin/risk-scoring/target-match-review/{reviewKey}/resolve",
                new { resolution = "Linked", primaryTargetKey = chosen });
            Assert.Equal(HttpStatusCode.NoContent, resolve.StatusCode);

            keys.Add(await ResolvedTargetKeyAsync(reviewKey));
            var chosenTarget = await client.GetFromJsonAsync<TargetResponse>($"/api/admin/targets/{chosen}");
            Assert.DoesNotContain(chosenTarget!.Identifiers, i => i.IdentifierValue == ip);
            var candidateTarget = await client.GetFromJsonAsync<TargetResponse>($"/api/admin/targets/{candidate}");
            Assert.Contains(candidateTarget!.Identifiers, i => i.IdentifierType == "IPAddress" && i.IdentifierValue == ip);
        }
        finally
        {
            await CleanUpAsync(keys);
        }
    }

    [Fact]
    public async Task Review_LinkedToCandidate_AndNewTarget_Succeed()
    {
        var client = Client("TestUser.Admin");
        var ip1 = $"10.97.{Random.Shared.Next(1, 254)}.{Random.Shared.Next(1, 254)}";
        var ip2 = $"10.96.{Random.Shared.Next(1, 254)}.{Random.Shared.Next(1, 254)}";
        var candidate = await CreateTargetAsync(client, $"MergeCandidate {Guid.NewGuid():N}", ("IPAddress", ip1), ("IPAddress", ip2));
        var newName = $"ReviewNew {Guid.NewGuid():N}";
        var keys = new List<int> { candidate };
        try
        {
            var linkKey = await QueueReviewAsync(client, ip1);
            var linked = await client.PostAsJsonAsync($"/api/admin/risk-scoring/target-match-review/{linkKey}/resolve", new { resolution = "Linked" });
            Assert.Equal(HttpStatusCode.NoContent, linked.StatusCode);
            keys.Add(await ResolvedTargetKeyAsync(linkKey));

            var newKey = await QueueReviewAsync(client, ip2);
            var created = await client.PostAsJsonAsync($"/api/admin/risk-scoring/target-match-review/{newKey}/resolve",
                new { resolution = "NewTarget", newTargetName = newName });
            Assert.Equal(HttpStatusCode.NoContent, created.StatusCode);

            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            var newTargetKey = await connection.QuerySingleAsync<int>("SELECT TargetKey FROM web.dim_target WHERE TargetName = @newName", new { newName });
            keys.Add(newTargetKey);
            // Both IP addresses stay on the candidate.
            var owners = (await connection.QueryAsync<(string Value, int TargetKey)>(
                "SELECT IdentifierValue, TargetKey FROM web.target_identifier WHERE IdentifierType = 'IPAddress' AND IdentifierValue IN @Ips",
                new { Ips = new[] { ip1, ip2 } })).ToDictionary(r => r.Value, r => r.TargetKey);
            Assert.Equal(candidate, owners[ip1]);
            Assert.Equal(candidate, owners[ip2]);
        }
        finally
        {
            await CleanUpAsync(keys);
        }
    }

    [Fact]
    public async Task Review_LinkedToMissingTarget_Is400()
    {
        var client = Client("TestUser.Admin");
        var ip = $"10.95.{Random.Shared.Next(1, 254)}.{Random.Shared.Next(1, 254)}";
        var candidate = await CreateTargetAsync(client, $"MergeCandidate {Guid.NewGuid():N}", ("IPAddress", ip));
        try
        {
            var reviewKey = await QueueReviewAsync(client, ip);
            var resolve = await client.PostAsJsonAsync($"/api/admin/risk-scoring/target-match-review/{reviewKey}/resolve",
                new { resolution = "Linked", primaryTargetKey = int.MaxValue });
            Assert.Equal(HttpStatusCode.BadRequest, resolve.StatusCode);
        }
        finally
        {
            await CleanUpAsync([candidate]);
        }
    }

    private static async Task<int> ResolvedTargetKeyAsync(int reviewKey)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleAsync<int>(
            "SELECT ResolvedTargetKey FROM web.target_match_review WHERE TargetMatchReviewKey = @reviewKey", new { reviewKey });
    }

    /// <summary>An inventory row matching only an existing IP address goes to the review queue (D-119 Phase B).</summary>
    private static async Task<int> QueueReviewAsync(HttpClient client, string ip)
    {
        var csv = $"TargetType,TargetName,RiskScore,IPAddress\nServer,Incoming {Guid.NewGuid():N},10,{ip}\n";
        var upload = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        upload.Add(file, "file", "review.csv");
        var response = await client.PostAsync("/api/admin/risk-scoring/import/target-inventory", upload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var pending = await client.GetFromJsonAsync<List<ReviewRow>>("/api/admin/risk-scoring/target-match-review");
        return Assert.Single(pending!, r => r.IdentifierValue == ip).TargetMatchReviewKey;
    }

    private sealed class SearchResult
    {
        public int TargetKey { get; set; }
        public string TargetName { get; set; } = "";
        public string TargetTypeDisplayName { get; set; } = "";
        public List<IdentifierRow> Identifiers { get; set; } = [];
    }

    private sealed class IdentifierRow
    {
        public string IdentifierType { get; set; } = "";
        public string IdentifierValue { get; set; } = "";
    }

    private sealed class TargetResponse
    {
        public int TargetKey { get; set; }
        public List<IdentifierRow> Identifiers { get; set; } = [];
    }

    private sealed class TargetTypeResponse
    {
        public int TargetTypeKey { get; set; }
        public string TypeCode { get; set; } = "";
    }

    private sealed class TargetKeyResponse
    {
        public int TargetKey { get; set; }
    }

    private sealed class ReviewRow
    {
        public int TargetMatchReviewKey { get; set; }
        public string IdentifierValue { get; set; } = "";
    }
}
