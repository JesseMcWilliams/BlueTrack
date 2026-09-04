using System.Collections.Concurrent;
using System.Text.Json;
using CyberArk.AAM.NetStandardPasswordSDK;
using CyberArk.AAM.NetStandardPasswordSDK.Exceptions;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Secrets;

/// <summary>
/// CyberArk CP integration (D-16/D-32/D-38/D-39/D-40, Design_Secrets_Storage.md).
/// Calls the local NetStandardPasswordSDK directly (CP is installed on the
/// app server itself, D-09) -- no network/REST call.
/// </summary>
public sealed class CyberArkCpSecretsProvider(SecretsStoreRepository secretsStoreRepository) : IVaultSecretProvider
{
    public string BackendType => "CyberArkCP";

    // D-49: fetch-first, cache-as-fallback -- always attempt a live GetPassword
    // call first (favoring freshness), and only fall back to the last
    // successfully-fetched value if that call fails with a transient error
    // (D-48: Vault/CP connectivity or a password change in progress).
    // In-memory only, per-process, cleared on app restart -- matches the
    // design's own note that a live secret held in memory "needs the same
    // care as any in-memory credential handling," so this doesn't persist it.
    private static readonly ConcurrentDictionary<string, SecretResult> Cache = new();

    // Case-insensitive: BackendSettings is free-form JSON an admin types into
    // a plain textarea (SecretsStoreConfiguration.vue) -- requiring exact
    // "AppId" casing there is fragile, unlike this app's MVC action
    // formatters, which already do case-insensitive/camelCase matching but
    // don't apply to this standalone JsonSerializer.Deserialize call.
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<SecretResult> GetSecretAsync(SecretQuery query)
    {
        var cacheKey = $"{query.Safe}|{query.Folder}|{query.Object}";

        var backend = await secretsStoreRepository.GetByTypeAsync("CyberArkCP");
        if (string.IsNullOrEmpty(backend?.BackendSettings))
        {
            throw new SecretRetrievalException(
                "CyberArk CP is not configured -- no AppID set in web.secrets_store.BackendSettings.",
                CyberArkErrorCategory.Other);
        }

        CyberArkCpSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<CyberArkCpSettings>(backend.BackendSettings, JsonOptions)
                ?? throw new JsonException("Deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new SecretRetrievalException("CyberArk CP BackendSettings could not be parsed as JSON.", CyberArkErrorCategory.Other, ex);
        }

        var request = new PSDKPasswordRequest
        {
            AppID = settings.AppId,
            Safe = query.Safe,
            Folder = query.Folder,
            Object = query.Object
        };

        try
        {
            // Synchronous native/local call -- no async overload exists on this SDK.
            var password = PasswordSDK.GetPassword(request);

            var result = new SecretResult(password.Content ?? [], password.UserName, password.Address, FromFallbackCache: false);
            Cache[cacheKey] = result;
            return await Task.FromResult(result);
        }
        catch (PSDKException ex)
        {
            var category = CyberArkErrorClassifier.Classify(ex.Reason ?? ex.Message);

            if (CyberArkErrorClassifier.IsTransient(category) && Cache.TryGetValue(cacheKey, out var cached))
            {
                return cached with { FromFallbackCache = true };
            }

            throw new SecretRetrievalException($"CyberArk CP request failed ({category}): {ex.Message}", category, ex);
        }
    }
}
