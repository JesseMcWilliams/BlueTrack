namespace BlueTrack.Api.Secrets;

/// <summary>
/// Deserialized from web.secrets_store.BackendSettings (BackendType =
/// 'CyberArkCP') -- AppID is admin-configured, not hardcoded (D-49).
/// </summary>
public sealed class CyberArkCpSettings
{
    public required string AppId { get; init; }
}
