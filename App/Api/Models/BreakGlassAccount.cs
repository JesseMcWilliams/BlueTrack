namespace BlueTrack.Api.Models;

/// <summary>D-216: one designated break-glass account.</summary>
public sealed class BreakGlassAccount
{
    public int BreakGlassAccountKey { get; init; }
    /// <summary>What BlueTrack sees at sign-in: a Windows SID, or an OIDC/SAML user identifier.</summary>
    public required string ExternalIdentifier { get; init; }
    public required string DisplayName { get; init; }
    public string? Notes { get; init; }
    public DateTime CreatedDate { get; init; }
    public string? CreatedByName { get; init; }
}

public sealed class SaveBreakGlassAccountRequest
{
    /// <summary>DOMAIN\name (or .\name for a local account), a SID, or an OIDC/SAML user identifier.</summary>
    public required string Account { get; init; }
    public string? DisplayName { get; init; }
    public string? Notes { get; init; }
}
