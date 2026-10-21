namespace BlueTrack.Api.Models;

/// <summary>
/// One row from dbo.vw_unresolved_entitlement_members
/// (Database/24_BlueTrack_EntitlementMemberResolution.sql): a Safe
/// entitlement whose member ID from the Entitlements export matched no
/// dim_user/dim_group ID (D-145). UnresolvedMemberId is that ID and
/// MemberName the export's name for it. D-232: ResolutionMethod says how it
/// was resolved anyway -- ByName, Manual (linked on the report), External
/// (labelled on the report) -- or is null while it is unresolved.
/// </summary>
public sealed class UnresolvedEntitlementMember
{
    public long EntitlementKey { get; init; }
    public int SafeKey { get; init; }
    public required string SafeName { get; init; }
    public required string MemberType { get; init; }
    public required string UnresolvedMemberId { get; init; }
    public string? MemberName { get; init; }
    public string? ResolutionMethod { get; init; }
    public int? UserKey { get; init; }
    public int? GroupKey { get; init; }
    public string? ResolvedToName { get; init; }
    public string? ResolutionNote { get; init; }
    public string? ResolvedByName { get; init; }
    public DateTime? ResolvedDate { get; init; }
    public DateTime? MembershipExpirationDate { get; init; }
    public DateTime SnapshotDate { get; init; }
    /// <summary>D-277: the source (PRIVCLOUD or SELFHOSTED). D-279: Resolve and Undo send it back.</summary>
    public string? SourceSystemCode { get; init; }
    public string? SourceSystemName { get; init; }
}

/// <summary>D-232: the four ways an unresolved member can be resolved, as stored in fact_safe_entitlement.ResolutionMethod.</summary>
public static class EntitlementResolutionMethods
{
    public const string ById = "ById";
    public const string ByName = "ByName";
    public const string Manual = "Manual";
    public const string External = "External";
}

/// <summary>D-232: POST .../resolve. Resolution is Linked (with UserKey for a User member, GroupKey for a Group) or External (with an optional Note). D-279: SourceSystemCode is PRIVCLOUD (the default) or SELFHOSTED.</summary>
public sealed class ResolveEntitlementMemberRequest
{
    public string? SourceSystemCode { get; init; }
    public string? MemberType { get; init; }
    public string? SourceMemberId { get; init; }
    public string? Resolution { get; init; }
    public int? UserKey { get; init; }
    public int? GroupKey { get; init; }
    public string? Note { get; init; }
}

/// <summary>D-232: POST .../unresolve -- removes the manual resolution. D-279: SourceSystemCode as on resolve.</summary>
public sealed class UnresolveEntitlementMemberRequest
{
    public string? SourceSystemCode { get; init; }
    public string? MemberType { get; init; }
    public string? SourceMemberId { get; init; }
}

/// <summary>D-232: one user or group of the member's source from the Resolve form's search.</summary>
public sealed class EntitlementPrincipal
{
    public int Key { get; init; }
    public required string Name { get; init; }
    public string? Detail { get; init; }
}
