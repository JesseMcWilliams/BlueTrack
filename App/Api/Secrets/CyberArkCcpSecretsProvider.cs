using System.Net.Http.Json;
using System.Text.Json;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Secrets;

/// <summary>
/// CyberArk Central Credential Provider integration (D-79/D-80). Unlike
/// CyberArk CP (a local SDK call, no network), CCP is the "Central
/// Credential Provider Web Service" -- a REST endpoint typically deployed
/// alongside PVWA. Contract confirmed 2026-09-04 against a real, live CCP
/// instance rather than assumed from documentation: GET
/// {BaseUrl}/AIMWebService/api/Accounts?AppID=...&Safe=...&Folder=...&Object=...
/// returns 200 with a JSON body containing Content (the secret) plus
/// non-secret metadata on success, or a non-200 status with
/// {"ErrorCode":"APPAP004E","ErrorMsg":"..."} on failure -- the same
/// APPAPnnnE codes D-48's error table already covers, confirmed by
/// triggering a real "not found" error and getting exactly that shape back.
/// </summary>
public sealed class CyberArkCcpSecretsProvider(
    IHttpClientFactory httpClientFactory,
    SecretsStoreRepository secretsStoreRepository,
    SecretFallbackCache fallbackCache) : IVaultSecretProvider
{
    public string BackendType => "CyberArkCCP";

    // D-49's fetch-first/cache-as-fallback pattern (SecretFallbackCache, D-202),
    // same as CyberArkCpSecretsProvider -- arguably even more relevant here
    // since this is a network call, not a local one.

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<SecretResult> GetSecretAsync(SecretQuery query)
    {
        query = query.WithCyberArkFolder();   // a blank folder is "Root"
        var backend = await secretsStoreRepository.GetByTypeAsync("CyberArkCCP");
        if (string.IsNullOrEmpty(backend?.BackendSettings))
        {
            throw new SecretRetrievalException(
                "CyberArk CCP is not configured -- no BaseUrl/AppID set in web.secrets_store.BackendSettings.",
                CyberArkErrorCategory.Other);
        }

        CyberArkCcpSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<CyberArkCcpSettings>(backend.BackendSettings, JsonOptions)
                ?? throw new JsonException("Deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new SecretRetrievalException("CyberArk CCP BackendSettings could not be parsed as JSON.", CyberArkErrorCategory.Other, ex);
        }

        // D-202: HTTPS only -- also refuses a row saved before the save-time check.
        if (SecretsBackendUrls.HttpsError("BaseUrl", settings.BaseUrl) is { } urlError)
        {
            throw new SecretRetrievalException($"CyberArk CCP: {urlError}", CyberArkErrorCategory.Other);
        }

        var cacheKey = SecretFallbackCache.Key(BackendType, settings.AppId, settings.BaseUrl, query);
        var url = $"{settings.BaseUrl.TrimEnd('/')}/AIMWebService/api/Accounts" +
                  $"?AppID={Uri.EscapeDataString(settings.AppId)}" +
                  $"&Safe={Uri.EscapeDataString(query.Safe)}" +
                  $"&Folder={Uri.EscapeDataString(query.Folder)}" +
                  $"&Object={Uri.EscapeDataString(query.Object)}";

        var client = httpClientFactory.CreateClient(nameof(CyberArkCcpSecretsProvider));

        HttpResponseMessage response;
        try
        {
            response = await client.GetAsync(url);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // A real connection failure (host unreachable, TLS handshake, or the
            // client's timeout, which throws TaskCanceledException) -- always
            // transient/VaultConnectivity-shaped, so it's worth falling back to
            // cache the same as an APPBC007E from the body would be.
            if (fallbackCache.TryGetFallback(cacheKey, out var cachedOnConnFailure))
            {
                return cachedOnConnFailure;
            }
            throw new SecretRetrievalException($"CyberArk CCP connection failed: {ex.Message}", CyberArkErrorCategory.VaultConnectivity, ex);
        }
        using var _ = response;

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            var category = CyberArkErrorClassifier.Classify(errorBody);

            if (CyberArkErrorClassifier.IsTransient(category) && fallbackCache.TryGetFallback(cacheKey, out var cached))
            {
                return cached;
            }

            throw new SecretRetrievalException($"CyberArk CCP request failed ({category}): {errorBody}", category);
        }

        CcpAccountResponse? body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<CcpAccountResponse>(JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new SecretRetrievalException("CyberArk CCP returned a response that isn't valid JSON.", CyberArkErrorCategory.Other, ex);
        }
        if (body is null)
        {
            throw new SecretRetrievalException("CyberArk CCP returned an empty response body.", CyberArkErrorCategory.Other);
        }

        var result = new SecretResult(
            (body.Content ?? "").ToCharArray(),
            body.UserName,
            body.Address,
            FromFallbackCache: false);
        fallbackCache.Store(cacheKey, result);
        return result;
    }

    // Field names match the real, live CCP response exactly (confirmed
    // 2026-09-04) -- extra fields (CreationMethod, PolicyID, Safe, Object,
    // ResetImmediately, RetriesCount, DeviceType, Folder,
    // PasswordChangeInProcess) are ignored, not modeled, since nothing here
    // consumes them yet.
    private sealed class CcpAccountResponse
    {
        public string? Content { get; init; }
        public string? UserName { get; init; }
        public string? Address { get; init; }
    }
}
