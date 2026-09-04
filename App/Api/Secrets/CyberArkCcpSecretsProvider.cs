using System.Collections.Concurrent;
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
    SecretsStoreRepository secretsStoreRepository) : IVaultSecretProvider
{
    public string BackendType => "CyberArkCCP";

    // D-49's fetch-first/cache-as-fallback pattern, same as CyberArkCpSecretsProvider --
    // arguably even more relevant here since this is a network call, not a local one.
    private static readonly ConcurrentDictionary<string, SecretResult> Cache = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<SecretResult> GetSecretAsync(SecretQuery query)
    {
        var cacheKey = $"{query.Safe}|{query.Folder}|{query.Object}";

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
        catch (HttpRequestException ex)
        {
            // A real connection failure (host unreachable, TLS handshake, etc.)
            // -- always transient/VaultConnectivity-shaped, so it's worth
            // falling back to cache the same as an APPBC007E from the body would be.
            if (Cache.TryGetValue(cacheKey, out var cachedOnConnFailure))
            {
                return cachedOnConnFailure with { FromFallbackCache = true };
            }
            throw new SecretRetrievalException($"CyberArk CCP connection failed: {ex.Message}", CyberArkErrorCategory.VaultConnectivity, ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            var category = CyberArkErrorClassifier.Classify(errorBody);

            if (CyberArkErrorClassifier.IsTransient(category) && Cache.TryGetValue(cacheKey, out var cached))
            {
                return cached with { FromFallbackCache = true };
            }

            throw new SecretRetrievalException($"CyberArk CCP request failed ({category}): {errorBody}", category);
        }

        var body = await response.Content.ReadFromJsonAsync<CcpAccountResponse>(JsonOptions)
            ?? throw new SecretRetrievalException("CyberArk CCP returned an empty response body.", CyberArkErrorCategory.Other);

        var result = new SecretResult(
            (body.Content ?? "").ToCharArray(),
            body.UserName,
            body.Address,
            FromFallbackCache: false);
        Cache[cacheKey] = result;
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
