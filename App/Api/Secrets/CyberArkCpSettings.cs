namespace BlueTrack.Api.Secrets;

/// <summary>
/// Deserialized from web.secrets_store.BackendSettings (BackendType =
/// 'CyberArkCP') -- AppID is admin-configured, not hardcoded (D-49).
/// </summary>
public sealed class CyberArkCpSettings
{
    public required string AppId { get; init; }
    /// <summary>D-309: sent as the request's Reason, which the Credential Provider writes to its audit log; blank sends none.</summary>
    public string? Reason { get; init; }
}
