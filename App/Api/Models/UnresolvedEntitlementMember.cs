namespace BlueTrack.Api.Models;

/// <summary>
/// One row from dbo.vw_unresolved_entitlement_members
/// (Database/15_BlueTrack_UnresolvedEntitlementMembersView.sql) -- a Safe
/// entitlement granted directly to a CyberArk Identity/Entra-federated
/// user or built-in cloud role, referenced by a GUID or role name that
/// never appears in stg_pc_users/stg_pc_groups (D-107). UnresolvedMemberId
/// is the raw source identifier; there is no display name to resolve it
/// to without a live call to CyberArk Identity Administration.
/// </summary>
public sealed class UnresolvedEntitlementMember
{
    public long EntitlementKey { get; init; }
    public int SafeKey { get; init; }
    public required string SafeName { get; init; }
    public required string MemberType { get; init; }
    public required string UnresolvedMemberId { get; init; }
    public DateTime? MembershipExpirationDate { get; init; }
    public DateTime SnapshotDate { get; init; }
}
