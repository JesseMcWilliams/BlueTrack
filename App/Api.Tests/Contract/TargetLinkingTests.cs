using System.Net;
using System.Net.Http.Json;
using System.Text;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-229: Target Match Review's Link / New Target / Ignore, linked targets
/// (hidden by default, Unlink, never relinked), "the same row" on re-import
/// (discovery source + name), and a linked pair scoring once at the
/// primary's risk. Every target uses a per-test token and is removed afterwards.
/// </summary>
public class TargetLinkingTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private const string ReviewUrl = "/api/admin/risk-scoring/target-match-review";
    private readonly BlueTrackWebApplicationFactory _factory;

    public TargetLinkingTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user = "TestUser.Admin")
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private static string NewIp() => $"10.93.{Random.Shared.Next(1, 254)}.{Random.Shared.Next(1, 254)}";

    private static async Task<int> CreateTargetAsync(HttpClient client, string name, int riskScore, params (string Type, string Value)[] identifiers)
    {
        var types = await client.GetFromJsonAsync<List<TargetTypeResponse>>("/api/admin/targets/target-types");
        var response = await client.PostAsJsonAsync("/api/admin/targets", new
        {
            targetTypeKey = types!.Single(t => t.TypeCode == "Server").TargetTypeKey,
            targetName = name,
            riskScore,
            identifiers = identifiers.Select(i => new { identifierType = i.Type, identifierValue = i.Value }).ToArray()
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<TargetKeyResponse>())!.TargetKey;
    }

    private static async Task<ImportResult> ImportAsync(HttpClient client, string csv)
    {
        var upload = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(csv));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/csv");
        upload.Add(file, "file", "linking.csv");
        var response = await client.PostAsync("/api/admin/risk-scoring/import/target-inventory", upload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<ImportResult>())!;
        Assert.Empty(result.Errors);
        return result;
    }

    private static string Row(string name, string source, string ip, string? hostname = null, int risk = 300) =>
        $"TargetType,TargetName,RiskScore,Description,DiscoverySource,IPAddress,Hostname\nDatabase,{name},{risk},Imported row,{source},{ip},{hostname}\n";

    private static async Task<List<ReviewRow>> PendingForAsync(HttpClient client, string ip) =>
        (await client.GetFromJsonAsync<List<ReviewRow>>(ReviewUrl))!.Where(r => r.IdentifierValue == ip).ToList();

    private static async Task<HttpResponseMessage> ResolveAsync(HttpClient client, int reviewKey, object body) =>
        await client.PostAsJsonAsync($"{ReviewUrl}/{reviewKey}/resolve", body);

    /// <summary>Removes the targets, their links and identifiers, and every review row for the IPs.</summary>
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

    private static async Task<int?> TargetKeyByNameAsync(string name)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        return await connection.QuerySingleOrDefaultAsync<int?>("SELECT TargetKey FROM web.dim_target WHERE TargetName = @name", new { name });
    }

    [Fact]
    public async Task Link_CreatesALinkedTargetFromTheImportedRow_TheIpStaysOnThePrimary_AndIsAudited()
    {
        var client = Client();
        var token = $"lnk{Guid.NewGuid():N}"[..14];
        var ip = NewIp();
        var primary = await CreateTargetAsync(client, $"{token}-primary", 800, ("IPAddress", ip));
        var keys = new List<int> { primary };
        try
        {
            var result = await ImportAsync(client, Row($"{token}-db", "CMDB", ip, $"{token}-host"));
            Assert.Equal(1, result.PendingReviewCount);

            var review = Assert.Single(await PendingForAsync(client, ip));
            Assert.Equal($"{token}-db", review.TargetName);
            Assert.Equal("CMDB", review.DiscoverySource);
            Assert.Equal(300, review.RiskScore);
            Assert.Equal("Database", review.TargetTypeDisplayName);
            Assert.Contains(review.Identifiers, i => i.IdentifierType == "Hostname" && i.IdentifierValue == $"{token}-host");

            var resolve = await ResolveAsync(client, review.TargetMatchReviewKey, new { resolution = "Linked" });
            Assert.Equal(HttpStatusCode.NoContent, resolve.StatusCode);

            var linked = (await TargetKeyByNameAsync($"{token}-db"))!.Value;
            keys.Add(linked);
            var linkedTarget = (await client.GetFromJsonAsync<TargetResponse>($"/api/admin/targets/{linked}"))!;
            Assert.Equal(primary, linkedTarget.LinkedToTargetKey);
            Assert.Equal($"{token}-primary", linkedTarget.LinkedToTargetName);
            Assert.Equal("CMDB", linkedTarget.DiscoverySource);
            Assert.Equal(300, linkedTarget.RiskScore);
            Assert.Equal("Imported row", linkedTarget.Description);
            Assert.Contains(linkedTarget.Identifiers, i => i.IdentifierType == "Hostname" && i.IdentifierValue == $"{token}-host");
            Assert.DoesNotContain(linkedTarget.Identifiers, i => i.IdentifierValue == ip);

            var primaryTarget = (await client.GetFromJsonAsync<TargetResponse>($"/api/admin/targets/{primary}"))!;
            Assert.Contains(primaryTarget.Identifiers, i => i.IdentifierType == "IPAddress" && i.IdentifierValue == ip);
            Assert.Null(primaryTarget.LinkedToTargetKey);
            var listed = Assert.Single(primaryTarget.LinkedTargets);
            Assert.Equal(linked, listed.TargetKey);

            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            Assert.Equal(1, await connection.QuerySingleAsync<int>("""
                SELECT COUNT(*) FROM web.audit_event e JOIN web.audit_field_change c ON c.AuditEventKey = e.AuditEventKey
                WHERE e.EntityName = 'dim_target' AND e.EntityKey = @Key AND c.FieldName = 'LinkedToTargetKey'
                  AND c.OldValue IS NULL AND c.NewValue = @Primary
                """, new { Key = linked.ToString(), Primary = primary.ToString() }));
            Assert.Equal(1, await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM web.audit_event WHERE EntityName = 'target_match_review' AND EntityKey = @Key AND Detail LIKE '%resolved as Linked%'",
                new { Key = review.TargetMatchReviewKey.ToString() }));
        }
        finally
        {
            await CleanUpAsync(keys, ip);
        }
    }

    [Fact]
    public async Task Reimport_OfTheSameSourceAndName_ResolvesToTheLinkedTarget_WithoutAReview()
    {
        var client = Client();
        var token = $"rei{Guid.NewGuid():N}"[..14];
        var ip = NewIp();
        var primary = await CreateTargetAsync(client, $"{token}-primary", 800, ("IPAddress", ip));
        var keys = new List<int> { primary };
        try
        {
            await ImportAsync(client, Row($"{token}-db", "CMDB", ip));
            var review = Assert.Single(await PendingForAsync(client, ip));
            Assert.Equal(HttpStatusCode.NoContent, (await ResolveAsync(client, review.TargetMatchReviewKey, new { resolution = "Linked" })).StatusCode);
            keys.Add((await TargetKeyByNameAsync($"{token}-db"))!.Value);

            // Same source and name, ignoring case and surrounding spaces.
            var again = await ImportAsync(client, Row($" {token.ToUpperInvariant()}-DB ", " cmdb", ip));
            Assert.Equal(1, again.MergedCount);
            Assert.Equal(0, again.PendingReviewCount);
            Assert.Empty(await PendingForAsync(client, ip));

            // A different source with the same name is a different row.
            var other = await ImportAsync(client, Row($"{token}-db", "OtherTool", ip));
            Assert.Equal(1, other.PendingReviewCount);
        }
        finally
        {
            await CleanUpAsync(keys, ip);
        }
    }

    [Fact]
    public async Task AStrongMatchOnALinkedTarget_MergesIntoIt_AndChangesNothingElse()
    {
        var client = Client();
        var token = $"str{Guid.NewGuid():N}"[..14];
        var ip = NewIp();
        var primary = await CreateTargetAsync(client, $"{token}-primary", 800, ("IPAddress", ip));
        var keys = new List<int> { primary };
        try
        {
            await ImportAsync(client, Row($"{token}-db", "CMDB", ip, $"{token}-host"));
            var review = Assert.Single(await PendingForAsync(client, ip));
            Assert.Equal(HttpStatusCode.NoContent, (await ResolveAsync(client, review.TargetMatchReviewKey, new { resolution = "Linked" })).StatusCode);
            var linked = (await TargetKeyByNameAsync($"{token}-db"))!.Value;
            keys.Add(linked);

            // Its Hostname matches the linked target: merged into it (a new FQDN added), name, risk and link untouched, the IP still the primary's.
            var csv = $"TargetType,TargetName,RiskScore,DiscoverySource,IPAddress,Hostname,FQDN\nServer,Renamed {token},999,Elsewhere,{ip},{token}-host,{token}.example.test\n";
            var result = await ImportAsync(client, csv);
            Assert.Equal(1, result.MergedCount);
            Assert.Empty(await PendingForAsync(client, ip));

            var target = (await client.GetFromJsonAsync<TargetResponse>($"/api/admin/targets/{linked}"))!;
            Assert.Equal($"{token}-db", target.TargetName);
            Assert.Equal(300, target.RiskScore);
            Assert.Equal(primary, target.LinkedToTargetKey);
            Assert.Contains(target.Identifiers, i => i.IdentifierType == "FQDN" && i.IdentifierValue == $"{token}.example.test");
            Assert.DoesNotContain(target.Identifiers, i => i.IdentifierValue == ip);
            Assert.Contains((await client.GetFromJsonAsync<TargetResponse>($"/api/admin/targets/{primary}"))!.Identifiers, i => i.IdentifierValue == ip);
        }
        finally
        {
            await CleanUpAsync(keys, ip);
        }
    }

    [Fact]
    public async Task Ignore_IsRemembered_AndAPendingRowIsNeverQueuedTwice()
    {
        var client = Client();
        var token = $"ign{Guid.NewGuid():N}"[..14];
        var ip = NewIp();
        var primary = await CreateTargetAsync(client, $"{token}-primary", 500, ("IPAddress", ip));
        try
        {
            Assert.Equal(1, (await ImportAsync(client, Row($"{token}-db", "CMDB", ip))).PendingReviewCount);
            Assert.Equal(1, (await ImportAsync(client, Row($"{token}-db", "CMDB", ip))).PendingReviewCount);
            var review = Assert.Single(await PendingForAsync(client, ip));

            Assert.Equal(HttpStatusCode.NoContent, (await ResolveAsync(client, review.TargetMatchReviewKey, new { resolution = "Ignored" })).StatusCode);

            var again = await ImportAsync(client, Row($"{token}-DB", "CMDB", ip));
            Assert.Equal(1, again.IgnoredCount);
            Assert.Equal(0, again.PendingReviewCount);
            Assert.Equal(1, again.SucceededCount);
            Assert.Empty(await PendingForAsync(client, ip));
            Assert.Null(await TargetKeyByNameAsync($"{token}-db"));
        }
        finally
        {
            await CleanUpAsync([primary], ip);
        }
    }

    [Fact]
    public async Task NewTarget_IsAnUnlinkedTargetFromTheImportedRow_AndReimportResolvesToIt()
    {
        var client = Client();
        var token = $"new{Guid.NewGuid():N}"[..14];
        var ip = NewIp();
        var candidate = await CreateTargetAsync(client, $"{token}-candidate", 500, ("IPAddress", ip));
        var keys = new List<int> { candidate };
        try
        {
            await ImportAsync(client, Row($"{token}-app", "CMDB", ip, $"{token}-host", risk: 250));
            var review = Assert.Single(await PendingForAsync(client, ip));
            Assert.Equal(HttpStatusCode.NoContent, (await ResolveAsync(client, review.TargetMatchReviewKey, new { resolution = "NewTarget" })).StatusCode);

            var created = (await TargetKeyByNameAsync($"{token}-app"))!.Value;
            keys.Add(created);
            var target = (await client.GetFromJsonAsync<TargetResponse>($"/api/admin/targets/{created}"))!;
            Assert.Null(target.LinkedToTargetKey);
            Assert.Equal(250, target.RiskScore);
            Assert.Contains(target.Identifiers, i => i.IdentifierValue == $"{token}-host");
            Assert.DoesNotContain(target.Identifiers, i => i.IdentifierValue == ip);
            var candidateTarget = (await client.GetFromJsonAsync<TargetResponse>($"/api/admin/targets/{candidate}"))!;
            Assert.Contains(candidateTarget.Identifiers, i => i.IdentifierValue == ip);
            Assert.Empty(candidateTarget.LinkedTargets);

            var again = await ImportAsync(client, Row($"{token}-app", "CMDB", ip));
            Assert.Equal(1, again.MergedCount);
            Assert.Empty(await PendingForAsync(client, ip));
        }
        finally
        {
            await CleanUpAsync(keys, ip);
        }
    }

    [Fact]
    public async Task Unlink_KeepsBothTargets_IsAudited_AndAReimportNeverRelinks()
    {
        var client = Client();
        var token = $"unl{Guid.NewGuid():N}"[..14];
        var ip = NewIp();
        var primary = await CreateTargetAsync(client, $"{token}-primary", 800, ("IPAddress", ip));
        var keys = new List<int> { primary };
        try
        {
            await ImportAsync(client, Row($"{token}-db", "CMDB", ip));
            var review = Assert.Single(await PendingForAsync(client, ip));
            Assert.Equal(HttpStatusCode.NoContent, (await ResolveAsync(client, review.TargetMatchReviewKey, new { resolution = "Linked", primaryTargetKey = primary })).StatusCode);
            var linked = (await TargetKeyByNameAsync($"{token}-db"))!.Value;
            keys.Add(linked);

            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/admin/targets/{linked}/unlink", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/admin/targets/{linked}/unlink", null)).StatusCode);
            Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsync($"/api/admin/targets/{primary}/unlink", null)).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync($"/api/admin/targets/{int.MaxValue}/unlink", null)).StatusCode);

            Assert.Null((await client.GetFromJsonAsync<TargetResponse>($"/api/admin/targets/{linked}"))!.LinkedToTargetKey);
            Assert.Empty((await client.GetFromJsonAsync<TargetResponse>($"/api/admin/targets/{primary}"))!.LinkedTargets);

            // The same row again is that (now separate) target, not a new review and not a new link.
            var again = await ImportAsync(client, Row($"{token}-db", "CMDB", ip));
            Assert.Equal(1, again.MergedCount);
            Assert.Null((await client.GetFromJsonAsync<TargetResponse>($"/api/admin/targets/{linked}"))!.LinkedToTargetKey);

            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            Assert.Equal(1, await connection.QuerySingleAsync<int>("""
                SELECT COUNT(*) FROM web.audit_event e JOIN web.audit_field_change c ON c.AuditEventKey = e.AuditEventKey
                WHERE e.EntityName = 'dim_target' AND e.EntityKey = @Key AND c.FieldName = 'LinkedToTargetKey'
                  AND c.OldValue = @Primary AND c.NewValue IS NULL
                """, new { Key = linked.ToString(), Primary = primary.ToString() }));
        }
        finally
        {
            await CleanUpAsync(keys, ip);
        }
    }

    [Fact]
    public async Task LinkedTargets_AreHiddenFromTheListCountsAndSearch_UnlessShown()
    {
        var client = Client("TestUser.Analyst");
        var token = $"hid{Guid.NewGuid():N}"[..14];
        var ip = NewIp();
        var primary = await CreateTargetAsync(client, $"{token}-primary", 800, ("IPAddress", ip));
        var keys = new List<int> { primary };
        try
        {
            await ImportAsync(client, Row($"{token}-db", "CMDB", ip));
            var review = Assert.Single(await PendingForAsync(client, ip));
            Assert.Equal(HttpStatusCode.NoContent, (await ResolveAsync(client, review.TargetMatchReviewKey, new { resolution = "Linked" })).StatusCode);
            var linked = (await TargetKeyByNameAsync($"{token}-db"))!.Value;
            keys.Add(linked);

            var hidden = await client.GetAsync("/api/admin/targets?sort=modifiedDate:desc&pageSize=500");
            var hiddenRows = (await hidden.Content.ReadFromJsonAsync<List<TargetResponse>>())!;
            Assert.DoesNotContain(hiddenRows, r => r.TargetKey == linked);
            Assert.Contains(hiddenRows, r => r.TargetKey == primary);

            var shown = await client.GetAsync("/api/admin/targets?sort=modifiedDate:desc&pageSize=500&linked=Show");
            var shownRow = Assert.Single((await shown.Content.ReadFromJsonAsync<List<TargetResponse>>())!, r => r.TargetKey == linked);
            Assert.Equal(primary, shownRow.LinkedToTargetKey);

            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            var all = await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.dim_target");
            var linkedCount = await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM web.vw_linked_target");
            Assert.Equal(all - linkedCount, int.Parse(hidden.Headers.GetValues("X-Total-Count").Single()));
            Assert.Equal(all - linkedCount, int.Parse(hidden.Headers.GetValues("X-Filtered-Count").Single()));
            Assert.Equal(all, int.Parse(shown.Headers.GetValues("X-Filtered-Count").Single()));

            var search = (await client.GetFromJsonAsync<List<TargetResponse>>($"/api/admin/targets/search?q={token}"))!;
            Assert.Contains(search, r => r.TargetKey == primary);
            Assert.DoesNotContain(search, r => r.TargetKey == linked);

            var hiddenKeys = (await client.GetFromJsonAsync<KeysResponse>("/api/admin/targets/keys"))!;
            var shownKeys = (await client.GetFromJsonAsync<KeysResponse>("/api/admin/targets/keys?linked=Show"))!;
            Assert.Equal(all - linkedCount, hiddenKeys.MatchingCount);
            Assert.Equal(all, shownKeys.MatchingCount);
        }
        finally
        {
            await CleanUpAsync(keys, ip);
        }
    }

    [Fact]
    public async Task ALinkedPair_ScoresOnceAtThePrimarysRisk_AndLinkChangesMarkAccountsStale()
    {
        var client = Client();
        var token = $"scr{Guid.NewGuid():N}"[..14];
        var ip = NewIp();
        var primary = await CreateTargetAsync(client, $"{token}-primary", 800, ("IPAddress", ip));
        var keys = new List<int> { primary };
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        long bothAccount = 0, linkedOnlyAccount = 0;
        int groupKey = 0;
        try
        {
            await ImportAsync(client, Row($"{token}-db", "CMDB", ip, risk: 100));
            var review = Assert.Single(await PendingForAsync(client, ip));
            Assert.Equal(HttpStatusCode.NoContent, (await ResolveAsync(client, review.TargetMatchReviewKey, new { resolution = "Linked" })).StatusCode);
            var linked = (await TargetKeyByNameAsync($"{token}-db"))!.Value;
            keys.Add(linked);

            bothAccount = await NewAccountAsync(connection, token);
            linkedOnlyAccount = await NewAccountAsync(connection, token);
            await connection.ExecuteAsync("""
                INSERT INTO web.account_target_map (AccountKey, TargetKey, SourceMethod) VALUES (@Both, @Primary, 'ManualImport'), (@Both, @Linked, 'ManualImport'), (@Only, @Linked, 'ManualImport');
                INSERT INTO web.account_risk_score (AccountKey, IsRiskScoreStale) VALUES (@Both, 0), (@Only, 0);
                """, new { Both = bothAccount, Only = linkedOnlyAccount, Primary = primary, Linked = linked });
            groupKey = await connection.QuerySingleAsync<int>(
                "INSERT INTO web.dim_access_group (GroupName, GroupIdentifier, GroupScope, BaseRiskScore, IsRiskScoreStale) OUTPUT inserted.AccessGroupKey VALUES (@Name, @Name, 'Domain', 10, 0)",
                new { Name = $"{token}-group" });
            await connection.ExecuteAsync("INSERT INTO web.access_group_target_map (AccessGroupKey, TargetKey) VALUES (@G, @P), (@G, @L)",
                new { G = groupKey, P = primary, L = linked });

            async Task<List<int>> ValuesAsync(long accountKey) => (await connection.QueryAsync<int>(
                "SELECT RiskValue FROM web.ufn_ReachableRiskValues('Account', @AccountKey) ORDER BY RiskValue DESC", new { AccountKey = accountKey })).ToList();

            // Both targets reached: the primary's 800, once. The linked target alone: also the primary's 800.
            Assert.Equal([800], await ValuesAsync(bothAccount));
            Assert.Equal([800], await ValuesAsync(linkedOnlyAccount));
            Assert.Equal([800, 10], (await connection.QueryAsync<int>(
                "SELECT RiskValue FROM web.ufn_ReachableRiskValues('AccessGroup', @G) ORDER BY RiskValue DESC", new { G = groupKey })).ToList());
            Assert.Equal([800, 10], (await connection.QueryAsync<int>("""
                DECLARE @Keys web.AccessGroupKeyList; INSERT INTO @Keys (AccessGroupKey) VALUES (@G);
                SELECT RiskValue FROM web.ufn_ReachableRiskValues_ForAccessGroupSet(@Keys) ORDER BY RiskValue DESC
                """, new { G = groupKey })).ToList());

            // Changing the primary's risk marks what reaches the linked target stale too.
            var primaryTarget = (await client.GetFromJsonAsync<TargetResponse>($"/api/admin/targets/{primary}"))!;
            var put = await client.PutAsJsonAsync($"/api/admin/targets/{primary}", new
            {
                targetTypeKey = primaryTarget.TargetTypeKey, targetName = primaryTarget.TargetName, riskScore = 900,
                identifiers = primaryTarget.Identifiers.Select(i => new { identifierType = i.IdentifierType, identifierValue = i.IdentifierValue }).ToArray()
            });
            Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
            Assert.True(await connection.QuerySingleAsync<bool>("SELECT IsRiskScoreStale FROM web.account_risk_score WHERE AccountKey = @A", new { A = linkedOnlyAccount }));

            // Unlink: the linked target counts at its own risk again, and its accounts and groups are stale.
            await connection.ExecuteAsync("UPDATE web.account_risk_score SET IsRiskScoreStale = 0 WHERE AccountKey IN (@A, @B); UPDATE web.dim_access_group SET IsRiskScoreStale = 0 WHERE AccessGroupKey = @G;",
                new { A = linkedOnlyAccount, B = bothAccount, G = groupKey });
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/api/admin/targets/{linked}/unlink", null)).StatusCode);
            Assert.Equal([900, 100], await ValuesAsync(bothAccount));
            Assert.Equal([100], await ValuesAsync(linkedOnlyAccount));
            Assert.Equal(2, await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM web.account_risk_score WHERE AccountKey IN (@A, @B) AND IsRiskScoreStale = 1", new { A = linkedOnlyAccount, B = bothAccount }));
            Assert.True(await connection.QuerySingleAsync<bool>("SELECT IsRiskScoreStale FROM web.dim_access_group WHERE AccessGroupKey = @G", new { G = groupKey }));
        }
        finally
        {
            await connection.ExecuteAsync("""
                DELETE FROM web.access_group_target_map WHERE AccessGroupKey = @G;
                DELETE FROM web.dim_access_group WHERE AccessGroupKey = @G;
                DELETE FROM web.account_target_map WHERE AccountKey IN (@A, @B);
                DELETE FROM web.account_risk_score WHERE AccountKey IN (@A, @B);
                DELETE FROM dbo.fact_account_progress_history WHERE AccountKey IN (@A, @B);
                DELETE FROM dbo.fact_account_progress WHERE AccountKey IN (@A, @B);
                DELETE FROM dbo.fact_account WHERE AccountKey IN (@A, @B);
                """, new { G = groupKey, A = linkedOnlyAccount, B = bothAccount });
            await CleanUpAsync(keys, ip);
        }
    }

    private static async Task<long> NewAccountAsync(SqlConnection connection, string token) =>
        await connection.QuerySingleAsync<long>(
            "INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted) OUTPUT inserted.AccountKey VALUES (1, @Id, @Id, 0)",
            new { Id = $"{token}-{Guid.NewGuid():N}" });

    [Fact]
    public async Task LegacyReview_WithoutStoredData_CanBeLinkedWithTheAnalystsName()
    {
        var client = Client();
        var token = $"leg{Guid.NewGuid():N}"[..14];
        var ip = NewIp();
        var primary = await CreateTargetAsync(client, $"{token}-primary", 800, ("IPAddress", ip));
        var keys = new List<int> { primary };
        try
        {
            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            var reviewKey = await connection.QuerySingleAsync<int>(
                "INSERT INTO web.target_match_review (CandidateTargetKey, IdentifierType, IdentifierValue) OUTPUT inserted.TargetMatchReviewKey VALUES (@P, 'IPAddress', @Ip)",
                new { P = primary, Ip = ip });

            var review = Assert.Single(await PendingForAsync(client, ip));
            Assert.Null(review.TargetName);
            Assert.Equal(HttpStatusCode.NoContent, (await ResolveAsync(client, reviewKey, new { resolution = "Linked", newTargetName = $"{token}-named" })).StatusCode);
            var linked = (await TargetKeyByNameAsync($"{token}-named"))!.Value;
            keys.Add(linked);
            Assert.Equal(primary, (await client.GetFromJsonAsync<TargetResponse>($"/api/admin/targets/{linked}"))!.LinkedToTargetKey);
        }
        finally
        {
            await CleanUpAsync(keys, ip);
        }
    }

    [Fact]
    public async Task Resolve_MergedIsGone_AndAMissingPrimaryIs400()
    {
        var client = Client();
        var token = $"bad{Guid.NewGuid():N}"[..14];
        var ip = NewIp();
        var primary = await CreateTargetAsync(client, $"{token}-primary", 800, ("IPAddress", ip));
        try
        {
            await ImportAsync(client, Row($"{token}-db", "CMDB", ip));
            var review = Assert.Single(await PendingForAsync(client, ip));
            Assert.Equal(HttpStatusCode.BadRequest, (await ResolveAsync(client, review.TargetMatchReviewKey, new { resolution = "Merged" })).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await ResolveAsync(client, review.TargetMatchReviewKey, new { resolution = "Linked", primaryTargetKey = int.MaxValue })).StatusCode);
            Assert.Single(await PendingForAsync(client, ip));
        }
        finally
        {
            await CleanUpAsync([primary], ip);
        }
    }

    [Theory]
    [InlineData("TestUser.Viewer")]
    [InlineData("TestUser.Approver")]
    public async Task UnlinkAndResolve_WithoutManageTargets_Are403(string user)
    {
        var client = Client(user);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/admin/targets/1/unlink", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"{ReviewUrl}/1/resolve", new { resolution = "Ignored" })).StatusCode);
    }

    private sealed class ImportResult
    {
        public int SucceededCount { get; set; }
        public int CreatedCount { get; set; }
        public int MergedCount { get; set; }
        public int PendingReviewCount { get; set; }
        public int IgnoredCount { get; set; }
        public List<object> Errors { get; set; } = [];
    }

    private sealed class ReviewRow
    {
        public int TargetMatchReviewKey { get; set; }
        public string IdentifierValue { get; set; } = "";
        public string? TargetName { get; set; }
        public string? TargetTypeDisplayName { get; set; }
        public int? RiskScore { get; set; }
        public string? DiscoverySource { get; set; }
        public List<IdentifierRow> Identifiers { get; set; } = [];
    }

    private sealed class IdentifierRow
    {
        public string IdentifierType { get; set; } = "";
        public string IdentifierValue { get; set; } = "";
    }

    private sealed class LinkedRow
    {
        public int TargetKey { get; set; }
    }

    private sealed class TargetResponse
    {
        public int TargetKey { get; set; }
        public int TargetTypeKey { get; set; }
        public string TargetName { get; set; } = "";
        public int RiskScore { get; set; }
        public string? Description { get; set; }
        public string? DiscoverySource { get; set; }
        public int? LinkedToTargetKey { get; set; }
        public string? LinkedToTargetName { get; set; }
        public List<IdentifierRow> Identifiers { get; set; } = [];
        public List<LinkedRow> LinkedTargets { get; set; } = [];
    }

    private sealed class KeysResponse
    {
        public int MatchingCount { get; set; }
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
}
