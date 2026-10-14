using System.Text;
using System.Text.Json;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Secrets;

/// <summary>
/// CyberArk Conjur integration (D-84) -- a placeholder framework, built
/// structurally against Conjur's publicly documented REST Authn API (CyberArk
/// publishes a .NET client, cyberark/conjur-api-dotnet, but it isn't used
/// here), not against a real running
/// Conjur instance. D-33 explicitly deferred Conjur's contract
/// "indefinitely, no timeline" -- this does NOT resolve that deferral or
/// claim the contract is confirmed; it's scaffolding built at the user's
/// explicit request 2026-09-04 ("use placeholders for now") so the admin
/// framework exists ahead of real values, the same way CyberArk CP/CCP
/// existed as designed-but-unbuilt before their own real connection
/// details arrived. Treat this as unverified until tested against a real
/// appliance.
///
/// Two-step flow per Conjur's Authn API:
/// 1. POST {ApplianceUrl}/authn/{Account}/{Login}/authenticate, body = the
///    raw API key, Content-Type text/plain -- returns a short-lived signed
///    token (base64, in the raw response body).
/// 2. GET {ApplianceUrl}/secrets/{Account}/variable/{url-encoded variable
///    id}, header Authorization: Token token="{Base64 of the token from
///    step 1}" -- returns the secret value as the raw response body.
///    D-215: the token is Base64-encoded (UTF-8) for the header, as
///    CyberArk's own .NET client does (conjur-api-dotnet, Client.cs
///    ApplyAuthentication); it used to be sent raw. Still unverified
///    against a real appliance.
///
/// SecretQuery's Safe/Folder/Object shape is reinterpreted as: Object = the
/// Conjur variable ID (Conjur's own identifiers already look like paths,
/// e.g. "myapp/db/password", so no separate Safe/Folder split is needed),
/// Safe/Folder = unused.
///
/// The API key follows the same DPAPI-protected-inline convention as the
/// other new backends (D-84): protected via ILocalSecretProtector, written
/// through the admin API's write-only PlaintextCredential field, never
/// echoed back on read.
/// </summary>
public sealed class CyberArkConjurSecretsProvider(
    IHttpClientFactory httpClientFactory,
    SecretsStoreRepository secretsStoreRepository,
    ILocalSecretProtector localSecretProtector) : IVaultSecretProvider
{
    public string BackendType => "CyberArkConjur";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<SecretResult> GetSecretAsync(SecretQuery query)
    {
        var backend = await secretsStoreRepository.GetByTypeAsync(BackendType);
        if (string.IsNullOrEmpty(backend?.BackendSettings))
        {
            throw new SecretRetrievalException(
                "CyberArk Conjur is not configured -- no ApplianceUrl/Account/Login set in web.secrets_store.BackendSettings.",
                CyberArkErrorCategory.Other);
        }

        CyberArkConjurSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<CyberArkConjurSettings>(backend.BackendSettings, JsonOptions)
                ?? throw new JsonException("Deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new SecretRetrievalException("CyberArk Conjur BackendSettings could not be parsed as JSON.", CyberArkErrorCategory.Other, ex);
        }

        if (string.IsNullOrWhiteSpace(settings.ApplianceUrl) || string.IsNullOrWhiteSpace(settings.Account) || string.IsNullOrWhiteSpace(settings.Login))
        {
            throw new SecretRetrievalException("CyberArk Conjur is not configured -- ApplianceUrl, Account, and Login are all required.", CyberArkErrorCategory.Other);
        }

        // D-202: HTTPS only -- the API key is POSTed to this URL.
        if (SecretsBackendUrls.HttpsError("ApplianceUrl", settings.ApplianceUrl) is { } urlError)
        {
            throw new SecretRetrievalException($"CyberArk Conjur: {urlError}", CyberArkErrorCategory.Other);
        }

        if (string.IsNullOrWhiteSpace(settings.ProtectedCredential))
        {
            throw new SecretRetrievalException("CyberArk Conjur has no stored API key configured.", CyberArkErrorCategory.Other);
        }

        string apiKey;
        try
        {
            apiKey = localSecretProtector.UnprotectStored(settings.ProtectedCredential);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new SecretRetrievalException(
                "CyberArk Conjur's stored API key could not be decrypted -- it may have been protected on a different machine.",
                CyberArkErrorCategory.Other, ex);
        }

        var applianceUrl = settings.ApplianceUrl.TrimEnd('/');
        var client = httpClientFactory.CreateClient(nameof(CyberArkConjurSecretsProvider));

        string token;
        try
        {
            var authenticateUrl = $"{applianceUrl}/authn/{Uri.EscapeDataString(settings.Account)}/{Uri.EscapeDataString(settings.Login)}/authenticate";
            using var authRequest = new HttpRequestMessage(HttpMethod.Post, authenticateUrl)
            {
                Content = new StringContent(apiKey, Encoding.UTF8, "text/plain")
            };
            var authResponse = await client.SendAsync(authRequest);
            if (!authResponse.IsSuccessStatusCode)
            {
                var body = await authResponse.Content.ReadAsStringAsync();
                throw new SecretRetrievalException(
                    $"CyberArk Conjur authentication failed ({(int)authResponse.StatusCode}): {body}",
                    ClassifyStatus(authResponse.StatusCode));
            }

            token = await authResponse.Content.ReadAsStringAsync();
        }
        catch (Exception ex) when (ex is not SecretRetrievalException)
        {
            // Catches HttpRequestException plus anything else HttpClient
            // can throw for a connection failure (e.g. TaskCanceledException
            // on timeout) -- AzureKeyVaultSecretsProvider found a real case
            // of an SDK wrapping connection failures in an unexpected
            // exception type when tested against an unreachable placeholder
            // endpoint (D-84); catching broadly here avoids the same class
            // of surprise turning into an unhandled 500.
            throw new SecretRetrievalException($"CyberArk Conjur connection failed: {ex.Message}", CyberArkErrorCategory.VaultConnectivity, ex);
        }

        try
        {
            var secretUrl = $"{applianceUrl}/secrets/{Uri.EscapeDataString(settings.Account)}/variable/{Uri.EscapeDataString(query.Object)}";
            using var secretRequest = new HttpRequestMessage(HttpMethod.Get, secretUrl);
            secretRequest.Headers.TryAddWithoutValidation("Authorization", AuthorizationHeaderValue(token));

            var secretResponse = await client.SendAsync(secretRequest);
            if (!secretResponse.IsSuccessStatusCode)
            {
                var body = await secretResponse.Content.ReadAsStringAsync();
                throw new SecretRetrievalException(
                    $"CyberArk Conjur secret request failed ({(int)secretResponse.StatusCode}): {body}",
                    ClassifyStatus(secretResponse.StatusCode));
            }

            var content = await secretResponse.Content.ReadAsStringAsync();
            return new SecretResult(content.ToCharArray(), UserName: settings.Login, Address: applianceUrl, FromFallbackCache: false);
        }
        catch (Exception ex) when (ex is not SecretRetrievalException)
        {
            throw new SecretRetrievalException($"CyberArk Conjur connection failed: {ex.Message}", CyberArkErrorCategory.VaultConnectivity, ex);
        }
    }

    /// <summary>D-215: the Authorization header for a raw authenticate response -- Token token="{Base64 of its UTF-8 bytes}".</summary>
    public static string AuthorizationHeaderValue(string rawToken) =>
        $"Token token=\"{Convert.ToBase64String(Encoding.UTF8.GetBytes(rawToken))}\"";

    private static CyberArkErrorCategory ClassifyStatus(System.Net.HttpStatusCode statusCode) => statusCode switch
    {
        System.Net.HttpStatusCode.NotFound => CyberArkErrorCategory.NotFound,
        System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden => CyberArkErrorCategory.AccessDenied,
        _ => CyberArkErrorCategory.Other
    };

    private sealed class CyberArkConjurSettings
    {
        public string ApplianceUrl { get; init; } = "";
        public string Account { get; init; } = "";
        public string Login { get; init; } = "";
        public string? ProtectedCredential { get; init; }
    }
}
