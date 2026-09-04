using System.Text.Json;
using Azure;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Secrets;

/// <summary>
/// Azure Key Vault integration (D-84) -- built as a placeholder framework
/// ahead of real connection details, per the user's explicit request
/// 2026-09-04: "Use placeholders for now, these will be populated after
/// deployment." Structurally real (the actual Azure SDK, not a stub), but
/// unverified against a live vault since none is reachable from this dev
/// environment yet -- same caveat this project already applies to CyberArk
/// Conjur (D-33).
///
/// SecretQuery's Safe/Folder/Object shape (built for CyberArk) doesn't map
/// naturally onto Key Vault -- reinterpreted here as: Object = the Key
/// Vault secret name, Folder = an optional specific version (empty/null
/// means "latest"), Safe = unused. See IVaultSecretProvider's own comment,
/// which already anticipated this reinterpretation.
///
/// Two supported auth methods (BackendSettings.AuthMethod), since neither
/// is obviously "the" right choice without knowing the target environment:
/// - ManagedIdentity (default): no stored credential at all -- works
///   automatically once the app is deployed to an Azure resource (or
///   Azure Arc-enabled server) with a managed identity assigned. Set
///   ClientId in BackendSettings only if using a user-assigned identity.
/// - ServicePrincipal: needs TenantId + ClientId (BackendSettings, not
///   secret) and a client secret. The secret follows the same
///   DPAPI-protected-inline convention as AwsSecretsManagerSecretsProvider
///   and CyberArkConjurSecretsProvider (D-84): never stored in plaintext,
///   protected via ILocalSecretProtector before being written into
///   BackendSettings.ProtectedCredential by the admin API's write-only
///   PlaintextCredential field (SecretsStoreController), and never echoed
///   back on read (SecretsStoreRepository redacts it).
/// </summary>
public sealed class AzureKeyVaultSecretsProvider(
    SecretsStoreRepository secretsStoreRepository,
    ILocalSecretProtector localSecretProtector) : IVaultSecretProvider
{
    public string BackendType => "AzureKeyVault";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<SecretResult> GetSecretAsync(SecretQuery query)
    {
        var backend = await secretsStoreRepository.GetByTypeAsync(BackendType);
        if (string.IsNullOrEmpty(backend?.BackendSettings))
        {
            throw new SecretRetrievalException(
                "Azure Key Vault is not configured -- no VaultUri set in web.secrets_store.BackendSettings.",
                CyberArkErrorCategory.Other);
        }

        AzureKeyVaultSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<AzureKeyVaultSettings>(backend.BackendSettings, JsonOptions)
                ?? throw new JsonException("Deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new SecretRetrievalException("Azure Key Vault BackendSettings could not be parsed as JSON.", CyberArkErrorCategory.Other, ex);
        }

        if (string.IsNullOrWhiteSpace(settings.VaultUri))
        {
            throw new SecretRetrievalException("Azure Key Vault is not configured -- VaultUri is empty.", CyberArkErrorCategory.Other);
        }

        var credential = BuildCredential(settings);
        var client = new SecretClient(new Uri(settings.VaultUri), credential);
        var version = string.IsNullOrWhiteSpace(query.Folder) ? null : query.Folder;

        try
        {
            var response = await client.GetSecretAsync(query.Object, version);
            return new SecretResult(response.Value.Value.ToCharArray(), UserName: null, Address: settings.VaultUri, FromFallbackCache: false);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new SecretRetrievalException($"Azure Key Vault secret '{query.Object}' was not found.", CyberArkErrorCategory.NotFound, ex);
        }
        catch (RequestFailedException ex) when (ex.Status is 401 or 403)
        {
            throw new SecretRetrievalException($"Azure Key Vault denied access to secret '{query.Object}'.", CyberArkErrorCategory.AccessDenied, ex);
        }
        catch (RequestFailedException ex)
        {
            throw new SecretRetrievalException($"Azure Key Vault request failed: {ex.Message}", CyberArkErrorCategory.VaultConnectivity, ex);
        }
        catch (AuthenticationFailedException ex)
        {
            throw new SecretRetrievalException($"Azure Key Vault authentication failed: {ex.Message}", CyberArkErrorCategory.AccessDenied, ex);
        }
        catch (Exception ex) when (ex is not SecretRetrievalException)
        {
            // The Azure SDK's retry policy wraps connection-level failures
            // (DNS resolution, TLS handshake) in AggregateException, not
            // RequestFailedException -- found by actually testing against
            // an unreachable placeholder VaultUri (D-84), not assumed.
            throw new SecretRetrievalException($"Azure Key Vault request failed: {ex.Message}", CyberArkErrorCategory.VaultConnectivity, ex);
        }
    }

    private Azure.Core.TokenCredential BuildCredential(AzureKeyVaultSettings settings)
    {
        if (!string.Equals(settings.AuthMethod, "ServicePrincipal", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(settings.ClientId)
                ? new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned)
                : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(settings.ClientId));
        }

        if (string.IsNullOrWhiteSpace(settings.TenantId) || string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ProtectedCredential))
        {
            throw new SecretRetrievalException(
                "Azure Key Vault ServicePrincipal auth requires TenantId, ClientId, and a stored client secret to be configured.",
                CyberArkErrorCategory.Other);
        }

        string clientSecret;
        try
        {
            clientSecret = localSecretProtector.Unprotect(settings.ProtectedCredential);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new SecretRetrievalException(
                "Azure Key Vault's stored client secret could not be decrypted -- it may have been protected on a different machine.",
                CyberArkErrorCategory.Other, ex);
        }

        return new ClientSecretCredential(settings.TenantId, settings.ClientId, clientSecret);
    }

    private sealed class AzureKeyVaultSettings
    {
        public string VaultUri { get; init; } = "";
        public string AuthMethod { get; init; } = "ManagedIdentity"; // "ManagedIdentity" | "ServicePrincipal"
        public string? TenantId { get; init; }
        public string? ClientId { get; init; }
        public string? ProtectedCredential { get; init; }
    }
}
