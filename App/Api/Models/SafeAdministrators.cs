namespace BlueTrack.Api.Models;

/// <summary>D-335: one person who holds Manage Safe or Manage Safe Members on one Safe (SafeAdministratorsRepository).</summary>
public sealed class SafeAdministratorRow
{
    public int SafeKey { get; init; }
    public required string SafeName { get; init; }
    public required string SourceSystemName { get; init; }
    /// <summary>The Vault user name, or DOMAIN\sAMAccountName for an AD user.</summary>
    public required string PersonName { get; init; }
    public string? DisplayName { get; init; }
    public bool IsEnabled { get; init; }
    /// <summary>Every way the person reaches the Safe with these rights: Direct, Via Vault group: X, Via AD group: X, joined with "; ".</summary>
    public required string AccessPaths { get; init; }
    public bool ManageSafe { get; init; }
    public bool ManageSafeMembers { get; init; }
    /// <summary>Holds every permission in SafeAdministratorsRepository.FullRightsPermissions through one of those ways.</summary>
    public bool FullRights { get; init; }
    /// <summary>The person is one of CyberArk's own users, such as Master or a component user (hidden unless asked for).</summary>
    public bool IsBuiltIn { get; init; }
}

/// <summary>D-335: one person over every Safe they hold those rights on.</summary>
public sealed class SafeAdministratorPerson
{
    public required string PersonName { get; init; }
    public string? DisplayName { get; init; }
    public bool IsEnabled { get; init; }
    public bool IsBuiltIn { get; init; }
    public int Safes { get; init; }
    public int ManageSafeSafes { get; init; }
    public int ManageSafeMembersSafes { get; init; }
    public int FullRightsSafes { get; init; }
}
