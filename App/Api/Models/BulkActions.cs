namespace BlueTrack.Api.Models;

// D-190: bulk actions on the Targets, Access Groups and Risk Exceptions
// lists, through the same selection bar as Account Progress (D-182). Every
// bulk action reports per item: how many changed, and which were skipped
// and why. One request may hold at most app_config.BulkEditMaxAccounts keys.

/// <summary>"Select all matching" for a list: the keys matching its filters, or none if more match than the bulk limit.</summary>
public sealed class BulkKeysResult
{
    public int MatchingCount { get; init; }
    public int MaxItems { get; init; }
    public required IReadOnlyList<int> Keys { get; init; }
}

public sealed class BulkActionResult
{
    public int Requested { get; init; }
    public int Changed { get; init; }
    public required IReadOnlyList<BulkSkippedItem> Skipped { get; init; }
}

public sealed class BulkSkippedItem
{
    public int Key { get; init; }
    public string? Name { get; init; }
    public required string Reason { get; init; }
}

/// <summary>Bulk delete (Targets, Access Groups): a reason is required and recorded on each item's audit event.</summary>
public sealed class BulkDeleteRequest
{
    public required IReadOnlyList<int> Keys { get; init; }
    public required string Reason { get; init; }
}

/// <summary>Bulk edit of Targets: only the fields named in Fields change.</summary>
public sealed class BulkEditTargetsRequest
{
    public required IReadOnlyList<int> Keys { get; init; }
    public required IReadOnlyList<string> Fields { get; init; }
    public int? TargetTypeKey { get; init; }
    public int? ApplicationKey { get; init; }
    public int? RiskScore { get; init; }
    public string? Description { get; init; }
}

/// <summary>Bulk edit of Access Groups: only the fields named in Fields change.</summary>
public sealed class BulkEditAccessGroupsRequest
{
    public required IReadOnlyList<int> Keys { get; init; }
    public required IReadOnlyList<string> Fields { get; init; }
    public string? GroupScope { get; init; }
    public int? BaseRiskScore { get; init; }
    public string? Description { get; init; }
}

/// <summary>Bulk re-approval of Risk Exceptions: a new review date for each selected Active exception.</summary>
public sealed class BulkExtendReviewRequest
{
    public required IReadOnlyList<int> Keys { get; init; }
    public DateTime NewReviewDate { get; init; }
}

/// <summary>Revoking a Risk Exception, one or many: a reason is always required (D-190).</summary>
public sealed class RevokeRiskExceptionRequest
{
    public required string Reason { get; init; }
}

public sealed class BulkRevokeRequest
{
    public required IReadOnlyList<int> Keys { get; init; }
    public required string Reason { get; init; }
}
