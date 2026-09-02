namespace BlueTrack.Api.Models;

/// <summary>One role bundle (web.app_role + its web.role_permission set), for the Roles & Permissions admin page.</summary>
public sealed class AppRoleSummary
{
    public int AppRoleKey { get; init; }
    public required string RoleName { get; init; }
    public string? Description { get; init; }
    public required IReadOnlyList<string> PermissionNames { get; init; }
}

public sealed class PermissionCatalogItem
{
    public int PermissionKey { get; init; }
    public required string PermissionName { get; init; }
    public string? Description { get; init; }
}

public sealed class SaveRoleRequest
{
    public required string RoleName { get; init; }
    public string? Description { get; init; }
    public required IReadOnlyList<string> PermissionNames { get; init; }
}
