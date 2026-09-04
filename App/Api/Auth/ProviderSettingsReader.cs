using System.Text.Json;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Auth;

/// <summary>
/// Small shared helper (D-84) for deserializing
/// identity_provider_config.ConfigurationValues into OidcProviderSettings /
/// SamlProviderSettings. Fails soft (empty string / null) rather than
/// throwing -- a malformed or absent config means "not usable yet," which
/// GroupIdentifierExtractor/the settings factories already treat as a safe
/// no-op, not an error worth crashing a request over.
/// </summary>
public static class ProviderSettingsReader
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static string ReadGroupClaimType(IdentityProviderConfig provider)
    {
        if (string.IsNullOrWhiteSpace(provider.ConfigurationValues))
        {
            return "";
        }

        try
        {
            return provider.ProviderType switch
            {
                "OIDC" => JsonSerializer.Deserialize<OidcProviderSettings>(provider.ConfigurationValues, JsonOptions)?.GroupsClaimType ?? "",
                "SAML" => JsonSerializer.Deserialize<SamlProviderSettings>(provider.ConfigurationValues, JsonOptions)?.GroupClaimType ?? "",
                _ => ""
            };
        }
        catch (JsonException)
        {
            return "";
        }
    }

    public static OidcProviderSettings? ReadOidc(string? configurationValues)
    {
        if (string.IsNullOrWhiteSpace(configurationValues))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OidcProviderSettings>(configurationValues, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static SamlProviderSettings? ReadSaml(string? configurationValues)
    {
        if (string.IsNullOrWhiteSpace(configurationValues))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<SamlProviderSettings>(configurationValues, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
