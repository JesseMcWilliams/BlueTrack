namespace BlueTrack.Api.Models;

/// <summary>One row from web.identity_group_role_map, joined for display.</summary>
public sealed class GroupRoleMappingSummary
{
    public int MappingKey { get; init; }
    public int ProviderKey { get; init; }
    public required string ProviderType { get; init; }
    public required string ProviderDisplayName { get; init; }
    public required string IdentityGroupName { get; init; } // the stored raw identifier (a SID for WindowsIntegrated, D-69)
    public required string RoleName { get; init; }

    /// <summary>
    /// D-179: DOMAIN\Group for a WindowsIntegrated SID, resolved when the
    /// list is read (so a renamed group shows its current name). Null for
    /// other providers, whose stored identifier is already readable, or
    /// when the SID can't be resolved.
    /// </summary>
    public string? GroupDisplayName { get; set; }
}

/// <summary>
/// Body for both the create endpoint and the standalone lookup/test tool
/// (Design_Authorization-Model.md's Admin UI Requirements) -- an admin
/// types a friendly group name, never a raw SID.
/// </summary>
public sealed class ResolveGroupRequest
{
    public required string GroupName { get; init; }

    /// <summary>D-204: which provider the group comes from; null means WindowsIntegrated, as before.</summary>
    public int? ProviderKey { get; init; }
}

public sealed class ResolveGroupResult
{
    public required string ResolvedAccountName { get; init; }

    /// <summary>The identifier stored and matched: a SID for WindowsIntegrated, the claim value as-is for OIDC/SAML (D-204).</summary>
    public required string Sid { get; init; }
    public required IReadOnlyList<string> CurrentRoleNames { get; init; }
    public required IReadOnlyList<string> CurrentPermissionNames { get; init; }
}

public sealed class CreateGroupRoleMappingRequest
{
    public required string GroupName { get; init; }
    public required string RoleName { get; init; }

    /// <summary>D-204: which provider the group comes from; null means WindowsIntegrated, as before.</summary>
    public int? ProviderKey { get; init; }
}

/// <summary>D-204: a provider an admin can map groups for (the Add Mapping page's dropdown).</summary>
public sealed class MappableProvider
{
    public int ProviderKey { get; init; }
    public required string ProviderType { get; init; }
    public required string DisplayName { get; init; }
    public bool IsEnabled { get; init; }

    /// <summary>The claim the groups come in (OIDC/SAML), from the provider's settings; null for WindowsIntegrated.</summary>
    public string? GroupClaimType { get; set; }
}
