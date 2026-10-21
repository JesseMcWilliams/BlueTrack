using System.Net;
using System.Net.Http.Json;
using BlueTrack.Api.Models;
using BlueTrack.Api.Tests.Integration;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-283/D-284: Admin > Progress Rules and Admin > Blueprint Stages, and the load step the rules drive. The stages are
/// shared by every test, so each test puts them (and the rules) back as they were.
/// </summary>
public class ProgressRulesTests : IClassFixture<BlueTrackWebApplicationFactory>, IAsyncLifetime
{
    private readonly BlueTrackWebApplicationFactory _factory;
    private List<(int StageKey, int StageOrder, string StageName, bool IsStartStage, string? Milestone)> _stages = [];
    private int _maxRuleKey;
    private string? _seedRuleEnabled;

    public ProgressRulesTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    public async Task InitializeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        _stages = (await connection.QueryAsync<(int, int, string, bool, string?)>(
            "SELECT StageKey, StageOrder, StageName, IsStartStage, Milestone FROM dbo.dim_blueprint_stage")).ToList();
        _maxRuleKey = await connection.QuerySingleAsync<int>("SELECT ISNULL(MAX(ProgressRuleKey), 0) FROM web.progress_rule");
        _seedRuleEnabled = await connection.QuerySingleOrDefaultAsync<string>("SELECT STRING_AGG(CONCAT(ProgressRuleKey, ':', CAST(IsEnabled AS INT)), ',') FROM web.progress_rule");
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("DELETE FROM web.progress_rule WHERE ProgressRuleKey > @max", new { max = _maxRuleKey });
        foreach (var pair in (_seedRuleEnabled ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split(':');
            await connection.ExecuteAsync("UPDATE web.progress_rule SET IsEnabled = @on WHERE ProgressRuleKey = @key", new { key = int.Parse(parts[0]), on = parts[1] == "1" });
        }
        var keys = _stages.Select(s => s.StageKey).ToList();
        await connection.ExecuteAsync("DELETE FROM dbo.dim_blueprint_stage WHERE StageKey NOT IN @keys AND NOT EXISTS (SELECT 1 FROM dbo.fact_account_progress p WHERE p.CurrentStageKey = StageKey)", new { keys });
        await connection.ExecuteAsync("UPDATE dbo.dim_blueprint_stage SET IsStartStage = 0, Milestone = NULL");
        foreach (var s in _stages)
        {
            await connection.ExecuteAsync("UPDATE dbo.dim_blueprint_stage SET StageOrder = @StageOrder, StageName = @StageName, IsStartStage = @IsStartStage, Milestone = @Milestone WHERE StageKey = @StageKey",
                new { s.StageKey, s.StageOrder, s.StageName, s.IsStartStage, s.Milestone });
        }
    }

    private int Stage(string name) => _stages.Single(s => s.StageName == name).StageKey;

    [Fact]
    public async Task WithoutManageProgressRules_BothPagesAreForbidden()
    {
        var viewer = Client("TestUser.Viewer");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/admin/progress-rules")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/admin/blueprint-stages")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client("TestUser.Admin").GetAsync("/api/admin/progress-rules")).StatusCode);
    }

    [Fact]
    public async Task TheBuiltInRule_IsSeeded_AndRulesAreValidated()
    {
        var admin = Client("TestUser.Admin");
        var rules = (await admin.GetFromJsonAsync<List<ProgressRule>>("/api/admin/progress-rules"))!;
        var seeded = Assert.Single(rules, r => r.RuleName == "In the Vault, not in a pending Safe");
        Assert.Equal(("Discovered", "Onboarded to Vault"), (seeded.FromStageName, seeded.ToStageName));
        Assert.Equal([("Source", "In", "PRIVCLOUD, SELFHOSTED"), ("SafeName", "NotContains", "_Pending")],
            seeded.Conditions.Select(c => (c.FieldName!, c.Operator!, c.Value!)));

        async Task<HttpStatusCode> Post(object body) => (await admin.PostAsJsonAsync("/api/admin/progress-rules", body)).StatusCode;
        var from = Stage("Onboarded to Vault");
        var to = Stage("Managed / Rotation Enabled");
        var ok = new[] { new { FieldName = "CPMStatus", Operator = "Equals", Value = "success" } };
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { RuleName = "", FromStageKey = from, ToStageKey = to, Conditions = ok }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { RuleName = "x", FromStageKey = to, ToStageKey = from, Conditions = ok }));   // backward
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { RuleName = "x", FromStageKey = from, ToStageKey = to, Conditions = Array.Empty<object>() }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { RuleName = "x", FromStageKey = from, ToStageKey = to, Conditions = new[] { new { FieldName = "Password", Operator = "Equals", Value = "x" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { RuleName = "x", FromStageKey = from, ToStageKey = to, Conditions = new[] { new { FieldName = "LastVerified", Operator = "WithinDays", Value = "soon" } } }));
        Assert.Equal(HttpStatusCode.BadRequest, await Post(new { RuleName = "x", FromStageKey = from, ToStageKey = to, Conditions = new[] { new { FieldName = "AutoManaged", Operator = "Contains", Value = "Yes" } } }));
        Assert.Equal(HttpStatusCode.Created, await Post(new { RuleName = "Managed by the CPM", FromStageKey = from, ToStageKey = to, Conditions = ok }));
    }

    [Fact]
    public async Task TheLoad_MovesAnAccountThroughMatchingRules_ForwardOnly_AndPreviewCountsIt()
    {
        var admin = Client("TestUser.Admin");
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var token = $"pr{Guid.NewGuid():N}"[..12];
        var accountKey = await connection.QuerySingleAsync<long>("""
            INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted, AutoManaged, CPMStatus, LastVerifiedDate)
            OUTPUT inserted.AccountKey
            VALUES (1, @token, @token, 0, 1, 'success', CAST(SYSUTCDATETIME() AS DATE));
            """, new { token });
        try
        {
            await connection.ExecuteAsync("""
                INSERT INTO fact_account_progress (AccountKey, CurrentStageKey, CurrentStatusKey)
                VALUES (@accountKey, @start, (SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'In Progress'))
                """, new { accountKey, start = Stage("Discovered") });
            // Only this test's account: the other rules are turned off while it runs.
            await connection.ExecuteAsync("UPDATE web.progress_rule SET IsEnabled = 0");
            var name = new { FieldName = "AccountName", Operator = "Equals", Value = token };
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/admin/progress-rules", new
            {
                RuleName = "test: in the Vault", FromStageKey = Stage("Discovered"), ToStageKey = Stage("Onboarded to Vault"), Conditions = new[] { name }
            })).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/admin/progress-rules", new
            {
                RuleName = "test: managed", FromStageKey = Stage("Onboarded to Vault"), ToStageKey = Stage("Managed / Rotation Enabled"),
                Conditions = new object[] { name, new { FieldName = "AutoManaged", Operator = "Equals", Value = "Yes" }, new { FieldName = "LastVerified", Operator = "WithinDays", Value = "7" } }
            })).StatusCode);
            // Never reached: the CPM status isn't "failure".
            Assert.Equal(HttpStatusCode.Created, (await admin.PostAsJsonAsync("/api/admin/progress-rules", new
            {
                RuleName = "test: compliant", FromStageKey = Stage("Managed / Rotation Enabled"), ToStageKey = Stage("Verified / Compliant"),
                Conditions = new object[] { name, new { FieldName = "CPMStatus", Operator = "Equals", Value = "failure" } }
            })).StatusCode);

            var preview = (await (await admin.PostAsJsonAsync("/api/admin/progress-rules/preview", new
            {
                FromStageKey = Stage("Discovered"), ToStageKey = Stage("Onboarded to Vault"), Conditions = new[] { name }
            })).Content.ReadFromJsonAsync<ProgressRulePreview>())!;
            Assert.Equal(1, preview.MatchCount);
            Assert.Equal(accountKey, Assert.Single(preview.Accounts).AccountKey);

            await connection.ExecuteAsync("EXEC usp_Load_AccountProgressAutoAdvance");

            // Two stages in one load, status kept, and the rule that moved it recorded.
            var after = await connection.QuerySingleAsync<(int Stage, string Status, string? Rule)>("""
                SELECT p.CurrentStageKey, s.StatusName, r.RuleName FROM fact_account_progress p
                JOIN dim_progress_status s ON s.StatusKey = p.CurrentStatusKey
                LEFT JOIN web.progress_rule r ON r.ProgressRuleKey = p.LastAdvancedByRuleKey
                WHERE p.AccountKey = @accountKey
                """, new { accountKey });
            Assert.Equal((Stage("Managed / Rotation Enabled"), "In Progress", (string?)"test: managed"), after);

            // Risk Accepted / Excluded is never moved.
            await connection.ExecuteAsync("""
                UPDATE fact_account_progress SET CurrentStageKey = @start, CurrentStatusKey = (SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'Risk Accepted / Excluded')
                WHERE AccountKey = @accountKey;
                EXEC usp_Load_AccountProgressAutoAdvance;
                """, new { accountKey, start = Stage("Discovered") });
            Assert.Equal(Stage("Discovered"), await connection.QuerySingleAsync<int>("SELECT CurrentStageKey FROM fact_account_progress WHERE AccountKey = @accountKey", new { accountKey }));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM fact_account_progress WHERE AccountKey = @accountKey; DELETE FROM fact_account WHERE AccountKey = @accountKey;", new { accountKey });
        }
    }

    [Fact]
    public async Task Stages_CanBeRenamedAddedReorderedAndDeleted_AndTheKpiFollowsItsMilestone()
    {
        var admin = Client("TestUser.Admin");
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var assessed = Stage("Assessed / Prioritized");

        // Rename: the KPI (by milestone) doesn't care.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/blueprint-stages/{Stage("Onboarded to Vault")}",
            new { StageName = "In the Vault", IsStartStage = false, Milestone = "Onboarded" })).StatusCode);
        Assert.Equal(1, await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM dbo.dim_blueprint_stage WHERE StageName = 'In the Vault' AND Milestone = 'Onboarded'"));

        // Add at the end, then move it to second place; a duplicate name is refused.
        var added = (await (await admin.PostAsJsonAsync("/api/admin/blueprint-stages", new { StageName = "Owner confirmed" })).Content.ReadFromJsonAsync<StageKeyResponse>())!.StageKey;
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/admin/blueprint-stages", new { StageName = "Owner confirmed" })).StatusCode);
        var order = _stages.OrderBy(s => s.StageOrder).Select(s => s.StageKey).ToList();
        order.Insert(1, added);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync("/api/admin/blueprint-stages/order", new { StageKeys = order })).StatusCode);
        var stages = (await admin.GetFromJsonAsync<List<BlueprintStage>>("/api/admin/blueprint-stages"))!;
        Assert.Equal(added, stages[1].StageKey);

        // Milestones stay in funnel order; the start stage can't be unset, only moved.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/admin/blueprint-stages/{assessed}",
            new { StageName = "Assessed / Prioritized", IsStartStage = false, Milestone = "Compliant" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync($"/api/admin/blueprint-stages/{Stage("Discovered")}",
            new { StageName = "Discovered", IsStartStage = false })).StatusCode);
        // A reorder that would send the built-in rule backward is refused.
        var backward = new List<int>(order);
        backward.Remove(Stage("Onboarded to Vault"));
        backward.Insert(0, Stage("Onboarded to Vault"));
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PutAsJsonAsync("/api/admin/blueprint-stages/order", new { StageKeys = backward })).StatusCode);

        // Moving the Onboarded milestone back to Assessed changes the KPI's count from where it counts.
        var before = await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM fact_account_progress p JOIN dim_blueprint_stage s ON s.StageKey = p.CurrentStageKey WHERE s.StageOrder >= dbo.fn_MilestoneStageOrder(N'Onboarded')");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync($"/api/admin/blueprint-stages/{assessed}",
            new { StageName = "Assessed / Prioritized", IsStartStage = false, Milestone = "Onboarded" })).StatusCode);
        Assert.Null(await connection.QuerySingleAsync<string?>("SELECT Milestone FROM dbo.dim_blueprint_stage WHERE StageName = 'In the Vault'"));
        var afterCount = await connection.QuerySingleAsync<int>("SELECT COUNT(*) FROM fact_account_progress p JOIN dim_blueprint_stage s ON s.StageKey = p.CurrentStageKey WHERE s.StageOrder >= dbo.fn_MilestoneStageOrder(N'Onboarded')");
        Assert.True(afterCount >= before);

        // Delete: a stage in use is refused; the empty new one goes.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.DeleteAsync($"/api/admin/blueprint-stages/{Stage("Discovered")}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/admin/blueprint-stages/{added}")).StatusCode);
        Assert.Equal(_stages.Count, (await admin.GetFromJsonAsync<List<BlueprintStage>>("/api/admin/blueprint-stages"))!.Count);
    }

    private sealed record StageKeyResponse(int StageKey);
}
