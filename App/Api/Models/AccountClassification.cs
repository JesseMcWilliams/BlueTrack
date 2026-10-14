namespace BlueTrack.Api.Models;

/// <summary>D-233: web.account_classification_config, the single settings row for account classification.</summary>
public sealed class AccountClassificationSettings
{
    /// <summary>Off by default: no class or owner is worked out (a manual override still shows), and discovery reads no extra attributes.</summary>
    public bool IsEnabled { get; init; }

    /// <summary>The class when no rule matches: Unknown (default) or Primary.</summary>
    public string DefaultAccountClass { get; init; } = "Unknown";

    /// <summary>Find a non-primary account's owner through a shared employee ID.</summary>
    public bool OwnerByEmployeeId { get; init; }

    /// <summary>employeeID (default) or employeeNumber.</summary>
    public string EmployeeIdAttribute { get; init; } = "employeeID";

    public DateTime? ModifiedDate { get; init; }
    public string? ModifiedByName { get; init; }
}

/// <summary>D-233: one row of web.account_classification_rule.</summary>
public sealed class AccountClassificationRule
{
    public int AccountClassificationRuleKey { get; init; }

    /// <summary>Attribute | Name | OU. Attribute rules are evaluated first, then Name, then OU, each in DisplayOrder.</summary>
    public required string RuleType { get; init; }

    /// <summary>Attribute: the allow-listed attribute. Name: sAMAccountName or userPrincipalName. OU: null.</summary>
    public string? AttributeName { get; init; }

    /// <summary>Attribute: Equals | Contains | StartsWith | IsSet. Name: StartsWith | EndsWith | Pattern. OU: Contains | EndsWith.</summary>
    public required string Operator { get; init; }
    public string? Value { get; init; }

    /// <summary>Primary | Secondary | Admin.</summary>
    public required string AccountClass { get; init; }
    public bool IsEnabled { get; init; }
    public int DisplayOrder { get; init; }
    public DateTime CreatedDate { get; init; }
    public DateTime? ModifiedDate { get; init; }
}

public sealed class SaveAccountClassificationSettingsRequest
{
    public bool IsEnabled { get; init; }
    public string? DefaultAccountClass { get; init; }
    public bool OwnerByEmployeeId { get; init; }
    public string? EmployeeIdAttribute { get; init; }
}

public sealed class SaveAccountClassificationRuleRequest
{
    public string? RuleType { get; init; }
    public string? AttributeName { get; init; }
    public string? Operator { get; init; }
    public string? Value { get; init; }
    public string? AccountClass { get; init; }
    public bool IsEnabled { get; init; } = true;
}

public sealed class MoveAccountClassificationRuleRequest
{
    /// <summary>up | down, within the rule's own type.</summary>
    public string? Direction { get; init; }
}

/// <summary>D-233: a discovered account's manual class/owner override; both null clears it.</summary>
public sealed class SaveDiscoveredAccountClassificationRequest
{
    /// <summary>Primary | Secondary | Admin | Unknown, or null to follow the rules.</summary>
    public string? AccountClass { get; init; }

    /// <summary>The owner's sAMAccountName (same domain), or null to follow the rules.</summary>
    public string? OwnerSamAccountName { get; init; }
}
