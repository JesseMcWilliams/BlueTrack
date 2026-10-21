namespace BlueTrack.Api.Models;

/// <summary>D-284: one Blueprint stage on Admin > Blueprint Stages, with what uses it (which decides whether it can be deleted).</summary>
public sealed class BlueprintStage
{
    public int StageKey { get; init; }
    public required string StageName { get; init; }
    public int StageOrder { get; init; }
    public bool IsStartStage { get; init; }
    /// <summary>Onboarded, Managed or Compliant: the KPI that counts accounts at this stage or later; null for none.</summary>
    public string? Milestone { get; init; }
    public int AccountCount { get; init; }
    public int RuleCount { get; init; }
    public int HistoryCount { get; init; }
}

/// <summary>D-284: POST (add: name only) and PUT (name, start stage, milestone).</summary>
public sealed class SaveBlueprintStageRequest
{
    public string? StageName { get; init; }
    public bool IsStartStage { get; init; }
    public string? Milestone { get; init; }
}

/// <summary>D-284: PUT order -- every stage key, first to last.</summary>
public sealed class ReorderBlueprintStagesRequest
{
    public IReadOnlyList<int>? StageKeys { get; init; }
}

/// <summary>D-283: one condition of a Progress Rule: the account's FieldName compared by Operator with Value.</summary>
public sealed class ProgressRuleCondition
{
    public string? FieldName { get; init; }
    public string? Operator { get; init; }
    public string? Value { get; init; }
}

/// <summary>D-283: a Progress Rule: accounts at FromStageKey move to ToStageKey (a later stage) when every condition matches.</summary>
public sealed class ProgressRule
{
    public int ProgressRuleKey { get; init; }
    public required string RuleName { get; init; }
    public int FromStageKey { get; init; }
    public string? FromStageName { get; init; }
    public int ToStageKey { get; init; }
    public string? ToStageName { get; init; }
    public bool IsEnabled { get; init; }
    public string? Description { get; init; }
    public string? ModifiedByName { get; init; }
    public DateTime ModifiedDate { get; init; }
    public IReadOnlyList<ProgressRuleCondition> Conditions { get; set; } = [];
}

public sealed class SaveProgressRuleRequest
{
    public string? RuleName { get; init; }
    public int? FromStageKey { get; init; }
    public int? ToStageKey { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string? Description { get; init; }
    public IReadOnlyList<ProgressRuleCondition>? Conditions { get; init; }
}

/// <summary>D-283: Test: how many accounts at the stage the conditions would move now, and up to 50 of them.</summary>
public sealed class ProgressRulePreview
{
    public int MatchCount { get; init; }
    public required IReadOnlyList<ProgressRulePreviewAccount> Accounts { get; init; }
}

public sealed class ProgressRulePreviewAccount
{
    public long AccountKey { get; init; }
    public string? AccountName { get; init; }
    public string? UserName { get; init; }
    public string? Address { get; init; }
    public string? SafeName { get; init; }
}

/// <summary>D-283: a field a condition can test, and the operators that apply to its kind (Text, Date, YesNo).</summary>
public sealed record ProgressRuleField(string FieldName, string Kind, IReadOnlyList<string> Operators);
