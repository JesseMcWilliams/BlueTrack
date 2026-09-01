namespace BlueTrack.Api.Models;

/// <summary>
/// BlueTrack's own record of a person who has logged into the web app --
/// distinct from dbo.dim_user, which holds CyberArk vault users pulled from
/// Privilege Cloud/Self-Hosted exports (D-59).
/// </summary>
public sealed class AppUser
{
    public int UserKey { get; init; }
    public int ProviderKey { get; init; }
    public required string ExternalIdentifier { get; init; }
    public string? DisplayName { get; init; }
    public string? Email { get; init; }
    public DateTime FirstLogin { get; init; }
    public DateTime LastLogin { get; init; }
}
