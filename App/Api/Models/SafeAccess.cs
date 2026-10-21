namespace BlueTrack.Api.Models;

/// <summary>
/// D-281/D-282: GET /api/account-progress/{accountKey}/safe-access -- who can reach the account's Safe
/// (dbo.vw_safe_access_people) and which CyberArk Applications can retrieve it (dbo.vw_cyberark_application_account_access).
/// AdReadAt: when the Safe's groups were last read from AD (null when none was).
/// </summary>
public sealed class AccountSafeAccess
{
    public required IReadOnlyList<SafeAccessPerson> People { get; init; }
    public required IReadOnlyList<AccountCyberArkApplication> Applications { get; init; }
    public DateTime? AdReadAt { get; init; }
}

/// <summary>One person and one way they reach the Safe: Direct, Via Vault group: X, or Via AD group: X (DomainName and SamAccountName set).</summary>
public sealed class SafeAccessPerson
{
    public required string PersonName { get; init; }
    public string? DisplayName { get; init; }
    public string? DomainName { get; init; }
    public string? SamAccountName { get; init; }
    public bool IsEnabled { get; init; }
    public required string AccessPath { get; init; }
    public bool CanUse { get; init; }
    public bool CanRetrieve { get; init; }
    public bool CanList { get; init; }
}

public sealed class AccountCyberArkApplication
{
    public int CyberArkApplicationKey { get; init; }
    public required string AppId { get; init; }
    public string? Description { get; init; }
    public bool? Disabled { get; init; }
    public required string AccessPath { get; init; }
}

/// <summary>D-282: one row of Reports > CyberArk Applications, with its authentication methods and how many accounts it can retrieve.</summary>
public sealed class CyberArkApplicationSummary
{
    public int CyberArkApplicationKey { get; init; }
    public required string AppId { get; init; }
    public string? Description { get; init; }
    public string? Location { get; init; }
    public bool? Disabled { get; init; }
    public string? ExpirationDate { get; init; }
    public int? AccessPermittedFrom { get; init; }
    public int? AccessPermittedTo { get; init; }
    public bool? AllowExtendedAuthenticationRestrictions { get; init; }
    public string? BusinessOwnerName { get; init; }
    public string? BusinessOwnerEmail { get; init; }
    public string? BusinessOwnerPhone { get; init; }
    public bool IsDeletedInSource { get; init; }
    public int AccountCount { get; init; }
    public int SafeCount { get; init; }
    public IReadOnlyList<CyberArkApplicationAuthentication> Authentications { get; set; } = [];
}

/// <summary>D-282: AuthValue is null for a hash and any type whose value isn't kept.</summary>
public sealed class CyberArkApplicationAuthentication
{
    public int CyberArkApplicationKey { get; init; }
    public required string AuthType { get; init; }
    public string? AuthValue { get; init; }
    public string? Comment { get; init; }
    public bool? IsFolder { get; init; }
    public bool? AllowInternalScripts { get; init; }
    public string? Subject { get; init; }
    public string? Issuer { get; init; }
    public string? SubjectAlternativeName { get; init; }
}
