namespace BlueTrack.Api.Secrets;

/// <summary>
/// Deserialized from web.secrets_store.BackendSettings (BackendType =
/// 'CyberArkCCP') -- both admin-configured, not hardcoded (D-49). BaseUrl
/// is the PVWA/CCP host, e.g. "https://pvwa.company.com" -- the actual
/// call hits "{BaseUrl}/AIMWebService/api/Accounts".
/// </summary>
public sealed class CyberArkCcpSettings
{
    public required string BaseUrl { get; init; }
    public required string AppId { get; init; }
}
