using Dapper;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Dna;

/// <summary>D-342: a rule that tracks the DNA accounts matching all its conditions after each import.</summary>
public sealed class DnaTrackRule
{
    public int DnaTrackRuleKey { get; init; }
    public string RuleName { get; init; } = "";
    public bool IsEnabled { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<ProgressRuleCondition> Conditions { get; set; } = [];
}

public sealed class SaveDnaTrackRuleRequest
{
    public string? RuleName { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string? Description { get; init; }
    public IReadOnlyList<ProgressRuleCondition>? Conditions { get; init; }
}

/// <summary>D-342 (70_BlueTrack_DnaFeedTrackRetention.sql): web.dna_track_rule and its conditions.</summary>
public sealed class DnaTrackRuleRepository(IDbConnectionFactory connectionFactory)
{
    public async Task<IReadOnlyList<DnaTrackRule>> GetAllAsync()
    {
        using var connection = connectionFactory.Create();
        using var multi = await connection.QueryMultipleAsync("""
            SELECT DnaTrackRuleKey, RuleName, IsEnabled, Description FROM web.dna_track_rule ORDER BY RuleName;
            SELECT DnaTrackRuleKey, FieldName, Operator, Value FROM web.dna_track_rule_condition ORDER BY DnaTrackRuleKey, ConditionOrder;
            """);
        var rules = (await multi.ReadAsync<DnaTrackRule>()).ToList();
        var conditions = (await multi.ReadAsync<(int Key, string FieldName, string Operator, string? Value)>()).ToLookup(c => c.Key);
        foreach (var r in rules)
            r.Conditions = conditions[r.DnaTrackRuleKey].Select(c => new ProgressRuleCondition { FieldName = c.FieldName, Operator = c.Operator, Value = c.Value }).ToList();
        return rules;
    }

    public async Task<bool> NameTakenAsync(string name, int? exceptKey)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteScalarAsync<bool>(
            "SELECT CASE WHEN EXISTS (SELECT 1 FROM web.dna_track_rule WHERE RuleName = @name AND (@exceptKey IS NULL OR DnaTrackRuleKey <> @exceptKey)) THEN 1 ELSE 0 END",
            new { name, exceptKey });
    }

    /// <summary>Creates (key null) or replaces a rule and its conditions; returns its key, or null when there's no such rule.</summary>
    public async Task<int?> SaveAsync(int? key, SaveDnaTrackRuleRequest request, int userKey)
    {
        using var connection = connectionFactory.Create();
        connection.Open();
        using var transaction = connection.BeginTransaction();
        var p = new { key, name = request.RuleName!.Trim(), request.IsEnabled, description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim(), userKey };
        int? saved = key is null
            ? await connection.QuerySingleAsync<int>("""
                INSERT INTO web.dna_track_rule (RuleName, IsEnabled, Description, ModifiedBy) OUTPUT inserted.DnaTrackRuleKey
                VALUES (@name, @IsEnabled, @description, @userKey)
                """, p, transaction)
            : await connection.QuerySingleOrDefaultAsync<int?>("""
                UPDATE web.dna_track_rule SET RuleName = @name, IsEnabled = @IsEnabled, Description = @description, ModifiedBy = @userKey, ModifiedAt = SYSUTCDATETIME()
                OUTPUT inserted.DnaTrackRuleKey WHERE DnaTrackRuleKey = @key
                """, p, transaction);
        if (saved is null) return null;
        await connection.ExecuteAsync("DELETE FROM web.dna_track_rule_condition WHERE DnaTrackRuleKey = @saved", new { saved }, transaction);
        await connection.ExecuteAsync("""
            INSERT INTO web.dna_track_rule_condition (DnaTrackRuleKey, ConditionOrder, FieldName, Operator, Value)
            VALUES (@saved, @order, @FieldName, @Operator, @Value)
            """, (request.Conditions ?? []).Select((c, i) => new
            {
                saved, order = i + 1, c.FieldName, c.Operator,
                Value = Rules.ProgressRuleOperatorsTakeNoValue(c.Operator) ? null : c.Value?.Trim()
            }), transaction);
        transaction.Commit();
        return saved;
    }

    public async Task<bool> DeleteAsync(int key)
    {
        using var connection = connectionFactory.Create();
        return await connection.ExecuteAsync("DELETE FROM web.dna_track_rule WHERE DnaTrackRuleKey = @key", new { key }) > 0;
    }

    private static class Rules
    {
        public static bool ProgressRuleOperatorsTakeNoValue(string? op) => op is not null && AccountProgress.ProgressRuleFields.NoValue.Contains(op);
    }
}
