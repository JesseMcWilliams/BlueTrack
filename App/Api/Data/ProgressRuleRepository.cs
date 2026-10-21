using System.Text.Json;
using Dapper;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Data;

/// <summary>Thrown for a change that would leave the stages or rules inconsistent; the message is shown to the admin.</summary>
public sealed class ProgressRuleConflictException(string message) : Exception(message);

/// <summary>
/// D-283/D-284 (49_BlueTrack_ProgressRulesAndStages.sql): the Blueprint stages (dbo.dim_blueprint_stage) and the
/// Progress Rules (web.progress_rule, web.progress_rule_condition) the nightly load applies.
/// </summary>
public sealed class ProgressRuleRepository(IDbConnectionFactory connectionFactory)
{
    public static readonly string[] Milestones = ["Onboarded", "Managed", "Compliant"];

    public async Task<IReadOnlyList<BlueprintStage>> GetStagesAsync()
    {
        using var connection = connectionFactory.Create();
        return (await connection.QueryAsync<BlueprintStage>("""
            SELECT s.StageKey, s.StageName, s.StageOrder, s.IsStartStage, s.Milestone,
                   (SELECT COUNT(*) FROM dbo.fact_account_progress p WHERE p.CurrentStageKey = s.StageKey) AS AccountCount,
                   (SELECT COUNT(*) FROM web.progress_rule r WHERE s.StageKey IN (r.FromStageKey, r.ToStageKey)) AS RuleCount,
                   (SELECT COUNT(*) FROM dbo.fact_account_progress_history h WHERE h.StageKey = s.StageKey) AS HistoryCount
            FROM dbo.dim_blueprint_stage s
            ORDER BY s.StageOrder, s.StageKey
            """)).ToList();
    }

    /// <summary>Adds a stage at the end; returns its key.</summary>
    public async Task<int> AddStageAsync(string name)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        await EnsureUniqueNameAsync(connection, transaction, name, null);
        var key = await connection.QuerySingleAsync<int>("""
            INSERT INTO dbo.dim_blueprint_stage (StageOrder, StageName)
            VALUES ((SELECT ISNULL(MAX(StageOrder), 0) + 1 FROM dbo.dim_blueprint_stage), @name);
            SELECT CAST(SCOPE_IDENTITY() AS INT);
            """, new { name }, transaction);
        transaction.Commit();
        return key;
    }

    /// <summary>
    /// Renames a stage and sets whether new accounts start there and which KPI milestone it carries; the flag or
    /// milestone moves off the stage that had it. Returns the stage as it was, or null when there's no such stage.
    /// </summary>
    public async Task<BlueprintStage?> UpdateStageAsync(int stageKey, string name, bool isStartStage, string? milestone)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var before = await connection.QuerySingleOrDefaultAsync<BlueprintStage>(
            "SELECT StageKey, StageName, StageOrder, IsStartStage, Milestone FROM dbo.dim_blueprint_stage WITH (UPDLOCK) WHERE StageKey = @stageKey",
            new { stageKey }, transaction);
        if (before is null) return null;
        if (before.IsStartStage && !isStartStage)
            throw new ProgressRuleConflictException("New accounts must start at some stage: mark another stage as the start stage instead.");
        await EnsureUniqueNameAsync(connection, transaction, name, stageKey);

        await connection.ExecuteAsync("""
            IF @isStartStage = 1 UPDATE dbo.dim_blueprint_stage SET IsStartStage = 0 WHERE StageKey <> @stageKey AND IsStartStage = 1;
            IF @milestone IS NOT NULL UPDATE dbo.dim_blueprint_stage SET Milestone = NULL WHERE StageKey <> @stageKey AND Milestone = @milestone;
            UPDATE dbo.dim_blueprint_stage SET StageName = @name, IsStartStage = @isStartStage, Milestone = @milestone WHERE StageKey = @stageKey;
            """, new { stageKey, name, isStartStage, milestone }, transaction);
        await EnsureMilestoneOrderAsync(connection, transaction);
        transaction.Commit();
        return before;
    }

    /// <summary>Puts the stages in this order (every stage key, once).</summary>
    public async Task ReorderStagesAsync(IReadOnlyList<int> stageKeys)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var existing = (await connection.QueryAsync<int>("SELECT StageKey FROM dbo.dim_blueprint_stage WITH (UPDLOCK)", transaction: transaction)).ToHashSet();
        if (stageKeys.Count != existing.Count || stageKeys.Distinct().Count() != stageKeys.Count || !stageKeys.All(existing.Contains))
            throw new ProgressRuleConflictException("Give every stage once, in the new order. Reload the page: the stages may have changed.");
        for (var i = 0; i < stageKeys.Count; i++)
        {
            await connection.ExecuteAsync("UPDATE dbo.dim_blueprint_stage SET StageOrder = @order WHERE StageKey = @key",
                new { order = i + 1, key = stageKeys[i] }, transaction);
        }
        await EnsureMilestoneOrderAsync(connection, transaction);
        var backward = await connection.QueryFirstOrDefaultAsync<string>("""
            SELECT TOP (1) r.RuleName FROM web.progress_rule r
            JOIN dbo.dim_blueprint_stage f ON f.StageKey = r.FromStageKey JOIN dbo.dim_blueprint_stage t ON t.StageKey = r.ToStageKey
            WHERE t.StageOrder <= f.StageOrder
            """, transaction: transaction);
        if (backward is not null)
            throw new ProgressRuleConflictException($"In this order the Progress Rule '{backward}' would move accounts back. Change its stages first.");
        transaction.Commit();
    }

    /// <summary>Deletes a stage nothing uses. Returns its name, or null when there's no such stage.</summary>
    public async Task<string?> DeleteStageAsync(int stageKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var stage = (await GetStagesAsync(connection, transaction)).FirstOrDefault(s => s.StageKey == stageKey);
        if (stage is null) return null;
        var why = stage switch
        {
            { IsStartStage: true } => "new accounts start at it",
            { Milestone: not null } => $"it carries the {stage.Milestone} milestone",
            { AccountCount: > 0 } => $"{stage.AccountCount} account(s) are at it",
            { RuleCount: > 0 } => $"{stage.RuleCount} Progress Rule(s) move accounts from or to it",
            { HistoryCount: > 0 } => "the progress history has accounts at it (rename it instead)",
            _ => null
        };
        if (why is not null) throw new ProgressRuleConflictException($"'{stage.StageName}' can't be deleted: {why}.");
        await connection.ExecuteAsync("""
            DELETE FROM dbo.dim_blueprint_stage WHERE StageKey = @stageKey;
            ;WITH o AS (SELECT StageOrder, ROW_NUMBER() OVER (ORDER BY StageOrder, StageKey) AS n FROM dbo.dim_blueprint_stage)
            UPDATE o SET StageOrder = n;
            """, new { stageKey }, transaction);
        transaction.Commit();
        return stage.StageName;
    }

    private static async Task<List<BlueprintStage>> GetStagesAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction) =>
        (await connection.QueryAsync<BlueprintStage>("""
            SELECT s.StageKey, s.StageName, s.StageOrder, s.IsStartStage, s.Milestone,
                   (SELECT COUNT(*) FROM dbo.fact_account_progress p WHERE p.CurrentStageKey = s.StageKey) AS AccountCount,
                   (SELECT COUNT(*) FROM web.progress_rule r WHERE s.StageKey IN (r.FromStageKey, r.ToStageKey)) AS RuleCount,
                   (SELECT COUNT(*) FROM dbo.fact_account_progress_history h WHERE h.StageKey = s.StageKey) AS HistoryCount
            FROM dbo.dim_blueprint_stage s WITH (UPDLOCK)
            """, transaction: transaction)).ToList();

    private static async Task EnsureUniqueNameAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction, string name, int? stageKey)
    {
        if (await connection.ExecuteScalarAsync<int>(
                "SELECT COUNT(*) FROM dbo.dim_blueprint_stage WHERE StageName = @name AND (@stageKey IS NULL OR StageKey <> @stageKey)",
                new { name, stageKey }, transaction) > 0)
            throw new ProgressRuleConflictException($"There's already a stage called '{name}'.");
    }

    /// <summary>The KPI funnel is nested, so Onboarded comes before Managed, which comes before Compliant.</summary>
    private static async Task EnsureMilestoneOrderAsync(System.Data.IDbConnection connection, System.Data.IDbTransaction transaction)
    {
        var orders = (await connection.QueryAsync<(string Milestone, int StageOrder)>(
            "SELECT Milestone, StageOrder FROM dbo.dim_blueprint_stage WHERE Milestone IS NOT NULL", transaction: transaction))
            .ToDictionary(m => m.Milestone, m => m.StageOrder);
        for (var i = 1; i < Milestones.Length; i++)
        {
            for (var j = 0; j < i; j++)
            {
                if (orders.TryGetValue(Milestones[i], out var later) && orders.TryGetValue(Milestones[j], out var earlier) && later <= earlier)
                    throw new ProgressRuleConflictException($"The {Milestones[i]} stage must come after the {Milestones[j]} stage.");
            }
        }
    }

    public async Task<IReadOnlyList<ProgressRule>> GetRulesAsync()
    {
        using var connection = connectionFactory.Create();
        using var multi = await connection.QueryMultipleAsync("""
            SELECT r.ProgressRuleKey, r.RuleName, r.FromStageKey, fs.StageName AS FromStageName, r.ToStageKey, ts.StageName AS ToStageName,
                   r.IsEnabled, r.Description, u.DisplayName AS ModifiedByName, r.ModifiedDate
            FROM web.progress_rule r
            JOIN dbo.dim_blueprint_stage fs ON fs.StageKey = r.FromStageKey
            JOIN dbo.dim_blueprint_stage ts ON ts.StageKey = r.ToStageKey
            LEFT JOIN web.app_user u ON u.UserKey = r.ModifiedBy
            ORDER BY fs.StageOrder, r.ProgressRuleKey;

            SELECT ProgressRuleKey, FieldName, Operator, Value FROM web.progress_rule_condition ORDER BY ProgressRuleKey, ProgressRuleConditionKey;
            """);
        var rules = (await multi.ReadAsync<ProgressRule>()).ToList();
        var conditions = (await multi.ReadAsync<(int ProgressRuleKey, string FieldName, string Operator, string? Value)>()).ToLookup(c => c.ProgressRuleKey);
        foreach (var rule in rules)
        {
            rule.Conditions = conditions[rule.ProgressRuleKey].Select(c => new ProgressRuleCondition { FieldName = c.FieldName, Operator = c.Operator, Value = c.Value }).ToList();
        }
        return rules;
    }

    /// <summary>The stage's position, or null when there's no such stage.</summary>
    public async Task<int?> GetStageOrderAsync(int stageKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<int?>("SELECT StageOrder FROM dbo.dim_blueprint_stage WHERE StageKey = @stageKey", new { stageKey });
    }

    /// <summary>Adds (ruleKey null) or replaces a rule and its conditions. Returns the key, or null when the rule to replace is gone.</summary>
    public async Task<int?> SaveRuleAsync(int? ruleKey, SaveProgressRuleRequest request, int userKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var p = new { ruleKey, RuleName = request.RuleName!.Trim(), request.FromStageKey, request.ToStageKey, request.IsEnabled, Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(), userKey };
        int? key = ruleKey is null
            ? await connection.QuerySingleAsync<int>("""
                INSERT INTO web.progress_rule (RuleName, FromStageKey, ToStageKey, IsEnabled, Description, ModifiedBy) VALUES (@RuleName, @FromStageKey, @ToStageKey, @IsEnabled, @Description, @userKey);
                SELECT CAST(SCOPE_IDENTITY() AS INT);
                """, p, transaction)
            : await connection.ExecuteAsync("""
                UPDATE web.progress_rule SET RuleName = @RuleName, FromStageKey = @FromStageKey, ToStageKey = @ToStageKey, IsEnabled = @IsEnabled, Description = @Description,
                    ModifiedBy = @userKey, ModifiedDate = SYSUTCDATETIME()
                WHERE ProgressRuleKey = @ruleKey
                """, p, transaction) == 0 ? null : ruleKey;
        if (key is null) return null;

        await connection.ExecuteAsync("DELETE FROM web.progress_rule_condition WHERE ProgressRuleKey = @key", new { key }, transaction);
        await connection.ExecuteAsync(
            "INSERT INTO web.progress_rule_condition (ProgressRuleKey, FieldName, Operator, Value) VALUES (@key, @FieldName, @Operator, @Value)",
            request.Conditions!.Select(c => new { key, c.FieldName, c.Operator, Value = AccountProgress.ProgressRuleFields.NoValue.Contains(c.Operator!) ? null : c.Value?.Trim() }),
            transaction);
        transaction.Commit();
        return key;
    }

    /// <summary>Returns the rule's name, or null when there's no such rule.</summary>
    public async Task<string?> DeleteRuleAsync(int ruleKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<string>(
            "DELETE FROM web.progress_rule OUTPUT deleted.RuleName WHERE ProgressRuleKey = @ruleKey", new { ruleKey });
    }

    public async Task<ProgressRulePreview> PreviewAsync(int fromStageKey, IReadOnlyList<ProgressRuleCondition> conditions)
    {
        using var connection = connectionFactory.Create();
        var json = JsonSerializer.Serialize(conditions.Select(c => new { c.FieldName, c.Operator, c.Value }));
        using var multi = await connection.QueryMultipleAsync("dbo.usp_Preview_ProgressRule",
            new { FromStageKey = fromStageKey, Conditions = json }, commandType: System.Data.CommandType.StoredProcedure, commandTimeout: 120);
        var count = await multi.ReadSingleAsync<int>();
        var accounts = (await multi.ReadAsync<ProgressRulePreviewAccount>()).ToList();
        return new ProgressRulePreview { MatchCount = count, Accounts = accounts };
    }
}
