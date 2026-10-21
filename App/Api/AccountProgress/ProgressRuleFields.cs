using BlueTrack.Api.Models;

namespace BlueTrack.Api.AccountProgress;

/// <summary>
/// D-283: the account properties a Progress Rule condition can test and the operators each kind takes. The names are
/// the ones dbo.fn_ProgressAccountFields gives (49_BlueTrack_ProgressRulesAndStages.sql); keep the two in step.
/// </summary>
public static class ProgressRuleFields
{
    public const string Text = "Text";
    public const string Date = "Date";
    public const string YesNo = "YesNo";

    private static readonly string[] TextOperators = ["Equals", "NotEquals", "Contains", "NotContains", "StartsWith", "In", "NotIn", "IsEmpty", "IsNotEmpty"];
    private static readonly string[] DateOperators = ["WithinDays", "OlderThanDays", "IsEmpty", "IsNotEmpty"];
    private static readonly string[] YesNoOperators = ["Equals", "IsEmpty", "IsNotEmpty"];

    /// <summary>Operators that take no value.</summary>
    public static readonly IReadOnlySet<string> NoValue = new HashSet<string>(["IsEmpty", "IsNotEmpty"], StringComparer.Ordinal);

    public static readonly IReadOnlyList<ProgressRuleField> All =
    [
        Field("Source", Text), Field("SafeName", Text), Field("PlatformId", Text), Field("PlatformName", Text),
        Field("AccountName", Text), Field("UserName", Text), Field("Address", Text),
        Field("AutoManaged", YesNo), Field("CPMStatus", Text), Field("SecretType", Text),
        Field("LastVerified", Date), Field("LastReconciled", Date), Field("LastChanged", Date),
        Field("LastCPMAction", Text), Field("LastCPMActionResult", Text), Field("LastCPMActionTime", Date),
        Field("DeviceType", Text), Field("CreationMethod", Text),
        Field("AccountType", Text), Field("SourceOfRecord", Text), Field("RiskLevel", Text), Field("Status", Text),
        Field("OwnerName", Text), Field("BusinessUnit", Text), Field("Application", Text)
    ];

    private static ProgressRuleField Field(string name, string kind) =>
        new(name, kind, kind switch { Date => DateOperators, YesNo => YesNoOperators, _ => TextOperators });

    /// <summary>Why the condition can't be saved, or null when it can.</summary>
    public static string? Problem(ProgressRuleCondition condition)
    {
        var field = All.FirstOrDefault(f => string.Equals(f.FieldName, condition.FieldName, StringComparison.Ordinal));
        if (field is null) return $"'{condition.FieldName}' isn't a field a condition can test.";
        if (condition.Operator is null || !field.Operators.Contains(condition.Operator)) return $"'{condition.Operator}' can't be used with {field.FieldName}.";
        if (NoValue.Contains(condition.Operator)) return null;

        var value = condition.Value?.Trim() ?? "";
        if (value.Length == 0) return $"{field.FieldName} {condition.Operator} needs a value.";
        if (value.Length > 400) return $"The value for {field.FieldName} can be at most 400 characters.";
        if (field.Kind == Date && (!int.TryParse(value, out var days) || days < 0 || days > 3650)) return $"{field.FieldName} needs a number of days from 0 to 3650.";
        if (field.Kind == YesNo && value is not ("Yes" or "No")) return $"{field.FieldName} is Yes or No.";
        return null;
    }
}
