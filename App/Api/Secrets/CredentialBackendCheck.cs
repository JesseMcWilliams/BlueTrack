using System.Text.Json;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Secrets;

/// <summary>
/// D-250: whether the Secrets Store backend a credential uses has its own settings. A CyberArk CP credential only
/// works once the CP backend has an AppID (Admin > Secrets Store), and a CCP one once it has a base URL and an AppID;
/// without them a lookup fails with "CyberArk CP is not configured", which is what the first Self-Hosted Direct pull
/// hit. Checked up front, so the Data Sources page can say so before anyone presses Pull now.
/// </summary>
public static class CredentialBackendCheck
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Ready is null when the backend needs nothing configured (Windows DPAPI, the others aren't checked here); otherwise whether it's set up, with a plain-words note when it isn't.</summary>
    public static async Task<(bool? Ready, string? Note)> CheckAsync(SecretsStoreRepository repository, string backendType)
    {
        switch (backendType)
        {
            case "CyberArkCP":
                {
                    var settings = Read<CyberArkCpSettings>((await repository.GetByTypeAsync(backendType))?.BackendSettings);
                    return string.IsNullOrWhiteSpace(settings?.AppId)
                        ? (false, "The CyberArk CP backend has no AppID. Set it on Admin > Secrets Store.")
                        : (true, null);
                }
            case "CyberArkCCP":
                {
                    var settings = Read<CyberArkCcpSettings>((await repository.GetByTypeAsync(backendType))?.BackendSettings);
                    return string.IsNullOrWhiteSpace(settings?.AppId) || string.IsNullOrWhiteSpace(settings?.BaseUrl)
                        ? (false, "The CyberArk CCP backend has no base URL or AppID. Set them on Admin > Secrets Store.")
                        : (true, null);
                }
            default:
                return (null, null);
        }
    }

    private static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }
}
