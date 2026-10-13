using System.Text.Json;

namespace BlueTrack.Api.Secrets;

/// <summary>
/// D-202: secrets backends are reached over HTTPS only. An http:// CCP URL
/// would return the password in clear text, and Conjur would POST its API key
/// in clear text. Checked when an admin saves the backend
/// (SecretsStoreController) and again by each provider, so a row saved before
/// this check can't be used either.
/// </summary>
public static class SecretsBackendUrls
{
    /// <summary>The settings field, per backend type, that holds the backend's URL.</summary>
    private static readonly Dictionary<string, string> UrlFieldByBackend = new(StringComparer.OrdinalIgnoreCase)
    {
        ["CyberArkCCP"] = "BaseUrl",
        ["CyberArkConjur"] = "ApplianceUrl",
        ["AzureKeyVault"] = "VaultUri"
    };

    /// <summary>Null when <paramref name="url"/> is an absolute https URL; otherwise why not.</summary>
    public static string? HttpsError(string fieldName, string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? null
            : $"{fieldName} must be an absolute https:// URL.";

    /// <summary>
    /// Null when this backend's URL (if it has one and it's set) is https;
    /// otherwise why not. A blank URL is left to the provider's own
    /// "not configured" error, as before.
    /// </summary>
    public static string? Validate(string backendType, string? backendSettings)
    {
        if (!UrlFieldByBackend.TryGetValue(backendType, out var field) || string.IsNullOrWhiteSpace(backendSettings)) return null;

        JsonElement root;
        try
        {
            root = JsonDocument.Parse(backendSettings).RootElement;
        }
        catch (JsonException)
        {
            return null; // the provider reports unparseable settings itself
        }
        if (root.ValueKind != JsonValueKind.Object) return null;

        foreach (var property in root.EnumerateObject())
        {
            if (!property.Name.Equals(field, StringComparison.OrdinalIgnoreCase)) continue;
            var url = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null;
            return string.IsNullOrWhiteSpace(url) ? null : HttpsError(field, url);
        }
        return null;
    }
}
