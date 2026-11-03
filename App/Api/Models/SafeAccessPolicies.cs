namespace BlueTrack.Api.Models;

/// <summary>D-300: a Safe role: the Safe permissions (dim_permission names) a member must hold; ExactMatch: and no others.</summary>
public sealed class SafeRole
{
    public int SafeRoleKey { get; init; }
    public required string RoleName { get; init; }
    public string? Description { get; init; }
    public bool ExactMatch { get; init; }
    public IReadOnlyList<string> Permissions { get; set; } = [];
    /// <summary>How many policy rules use it (a role in use can't be deleted).</summary>
    public int RuleCount { get; init; }
}

public sealed class SaveSafeRoleRequest
{
    public string? RoleName { get; init; }
    public string? Description { get; init; }
    public bool ExactMatch { get; init; }
    public IReadOnlyList<string>? Permissions { get; init; }
}

/// <summary>
/// D-300: a Safe access policy: the Safes whose names match SafeNamePattern (* is any text) are checked against its
/// rules; the first enabled policy in SortOrder whose pattern matches a Safe applies to it.
/// </summary>
public sealed class SafeAccessPolicy
{
    public int SafeAccessPolicyKey { get; init; }
    public required string PolicyName { get; init; }
    public required string SafeNamePattern { get; init; }
    public int SortOrder { get; init; }
    public bool IsEnabled { get; init; }
    public string? Description { get; init; }
    public string? ModifiedByName { get; init; }
    public DateTime ModifiedDate { get; init; }
    public IReadOnlyList<SafeAccessPolicyRule> Rules { get; set; } = [];
}

/// <summary>
/// D-300: Required: a member whose name matches MemberPattern ({SafeName} is the Safe's name, * any text) holds the
/// role (and, with RequirePeople, has people in it). Forbidden: no member matching it (holding the role, if one is given).
/// </summary>
public sealed class SafeAccessPolicyRule
{
    public const string Required = "Required";
    public const string Forbidden = "Forbidden";

    public string? RuleType { get; init; }
    public string? MemberPattern { get; init; }
    public int? SafeRoleKey { get; init; }
    public string? SafeRoleName { get; init; }
    public bool RequirePeople { get; init; }
}

public sealed class SaveSafeAccessPolicyRequest
{
    public string? PolicyName { get; init; }
    public string? SafeNamePattern { get; init; }
    public int SortOrder { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string? Description { get; init; }
    public IReadOnlyList<SafeAccessPolicyRule>? Rules { get; init; }
}

/// <summary>D-300: one Safe on Reports > Safe Access: the policy that applied, the result and why it failed.</summary>
public sealed class SafeAccessReportRow
{
    public int SafeKey { get; init; }
    public required string SafeName { get; init; }
    public string? Source { get; init; }
    public string? PolicyName { get; init; }
    /// <summary>Null when no policy applies.</summary>
    public bool? IsCompliant { get; init; }
    public DateTime CheckedAt { get; init; }
    public int AccountCount { get; init; }
    public int ReviewedCount { get; init; }
    public IReadOnlyList<string> Problems { get; set; } = [];
}

/// <summary>D-300/D-301: an account's Safe check and its compliance review, for the account page.</summary>
public sealed class AccountCompliance
{
    public string? SafeName { get; init; }
    public string? PolicyName { get; init; }
    public bool? IsCompliant { get; init; }
    public DateTime? CheckedAt { get; init; }
    public IReadOnlyList<string> Problems { get; set; } = [];
    public string? ReviewedByName { get; init; }
    public DateTime? ReviewedAt { get; init; }
    public string? ReviewNote { get; init; }
}

public sealed class ComplianceReviewRequest
{
    public string? Note { get; init; }
}
