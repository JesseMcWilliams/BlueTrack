namespace BlueTrack.Api.Models;

/// <summary>
/// web.credential (D-116) -- a named credential this app authenticates *as*
/// (the SMTP account, the LDAP bind account, and whatever else needs one
/// later), distinct from web.secrets_store's single active backend for
/// resolving *privileged-account* secrets CyberArk/a vault manages. Never
/// returns HasPassword's real value or a vault reference's raw fields
/// unredacted -- see CredentialRepository.Redact.
/// </summary>
public sealed class Credential
{
    public int CredentialKey { get; init; }
    public required string CredentialName { get; init; }
    public required string BackendType { get; init; }
    public string? Username { get; init; }
    public bool HasPassword { get; init; }
    public string? ScopePreference { get; init; }
    public string? CurrentScope { get; init; }
    public string? VaultSafe { get; init; }
    public string? VaultFolder { get; init; }
    public string? VaultObject { get; init; }
    public DateTime? ModifiedDate { get; init; }
}

public sealed class SaveCredentialRequest
{
    public required string CredentialName { get; init; }
    public required string BackendType { get; init; }
    public string? Username { get; init; }

    /// <summary>Write-only -- left blank keeps whatever password (if any) is already stored. DPAPI only.</summary>
    public string? PlaintextPassword { get; init; }

    /// <summary>DPAPI only -- 'Machine' or 'User'.</summary>
    public string? ScopePreference { get; init; }

    public string? VaultSafe { get; init; }
    public string? VaultFolder { get; init; }
    public string? VaultObject { get; init; }
}
