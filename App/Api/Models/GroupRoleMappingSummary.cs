namespace BlueTrack.Api.Models;

/// <summary>One row from web.identity_group_role_map, joined for display.</summary>
public sealed class GroupRoleMappingSummary
{
    public int MappingKey { get; init; }
    public required string ProviderType { get; init; }
    public required string IdentityGroupName { get; init; } // the stored raw identifier (a SID for WindowsIntegrated, D-69)
    public required string RoleName { get; init; }
}

/// <summary>
/// Body for both the create endpoint and the standalone lookup/test tool
/// (Design_Authorization_Model.md's Admin UI Requirements) -- an admin
/// types a friendly group name, never a raw SID.
/// </summary>
public sealed class ResolveGroupRequest
{
    public required string GroupName { get; init; }
}

public sealed class ResolveGroupResult
{
    public required string ResolvedAccountName { get; init; }
    public required string Sid { get; init; }
    public required IReadOnlyList<string> CurrentRoleNames { get; init; }
    public required IReadOnlyList<string> CurrentPermissionNames { get; init; }
}

public sealed class CreateGroupRoleMappingRequest
{
    public required string GroupName { get; init; }
    public required string RoleName { get; init; }
}
