using System.Text.Json;
using Amazon;
using Amazon.Runtime;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Secrets;

/// <summary>
/// AWS Secrets Manager integration (D-84) -- a placeholder framework built
/// ahead of real connection details (region, auth method), matching
/// AzureKeyVaultSecretsProvider's same status: real AWS SDK calls, not a
/// stub, but unverified against a live account since none is reachable
/// from this dev environment yet.
///
/// SecretQuery's Safe/Folder/Object shape is reinterpreted as: Object = the
/// secret's name or ARN, Folder = an optional VersionStage (e.g.
/// "AWSCURRENT"/"AWSPREVIOUS" -- empty/null means Secrets Manager's own
/// default, AWSCURRENT), Safe = unused.
///
/// Two supported auth methods (BackendSettings.AuthMethod):
/// - IamRole (default): no stored credential -- relies on the AWS SDK's
///   default credential provider chain (EC2/ECS instance role, environment
///   variables, etc.), appropriate once this app is actually running on
///   AWS compute with a role attached.
/// - AccessKey: an explicit access key ID (BackendSettings, not secret)
///   plus a secret access key, which follows the same
///   DPAPI-protected-inline convention as the other new backends (D-84):
///   protected via ILocalSecretProtector, written through the admin API's
///   write-only PlaintextCredential field, never echoed back on read.
/// </summary>
public sealed class AwsSecretsManagerSecretsProvider(
    SecretsStoreRepository secretsStoreRepository,
    ILocalSecretProtector localSecretProtector) : IVaultSecretProvider
{
    public string BackendType => "AwsSecretsManager";

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task<SecretResult> GetSecretAsync(SecretQuery query)
    {
        var backend = await secretsStoreRepository.GetByTypeAsync(BackendType);
        if (string.IsNullOrEmpty(backend?.BackendSettings))
        {
            throw new SecretRetrievalException(
                "AWS Secrets Manager is not configured -- no Region set in web.secrets_store.BackendSettings.",
                CyberArkErrorCategory.Other);
        }

        AwsSecretsManagerSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<AwsSecretsManagerSettings>(backend.BackendSettings, JsonOptions)
                ?? throw new JsonException("Deserialized to null.");
        }
        catch (JsonException ex)
        {
            throw new SecretRetrievalException("AWS Secrets Manager BackendSettings could not be parsed as JSON.", CyberArkErrorCategory.Other, ex);
        }

        if (string.IsNullOrWhiteSpace(settings.Region))
        {
            throw new SecretRetrievalException("AWS Secrets Manager is not configured -- Region is empty.", CyberArkErrorCategory.Other);
        }

        var regionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
        using var client = BuildClient(settings, regionEndpoint);

        var request = new GetSecretValueRequest { SecretId = query.Object };
        if (!string.IsNullOrWhiteSpace(query.Folder))
        {
            request.VersionStage = query.Folder;
        }

        try
        {
            var response = await client.GetSecretValueAsync(request);
            var content = response.SecretString ?? "";
            return new SecretResult(content.ToCharArray(), UserName: null, Address: settings.Region, FromFallbackCache: false);
        }
        catch (ResourceNotFoundException ex)
        {
            throw new SecretRetrievalException($"AWS Secrets Manager secret '{query.Object}' was not found.", CyberArkErrorCategory.NotFound, ex);
        }
        catch (AmazonSecretsManagerException ex) when (
            ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden ||
            ex.ErrorCode is "UnrecognizedClientException" or "AccessDeniedException" or "InvalidSignatureException")
        {
            // Confirmed 2026-09-04 against a real (rejecting) AWS Secrets
            // Manager endpoint: an invalid access key returns HTTP 400 with
            // ErrorCode "UnrecognizedClientException", not 401/403 -- the
            // status-code check alone missed this.
            throw new SecretRetrievalException($"AWS Secrets Manager denied access to secret '{query.Object}'.", CyberArkErrorCategory.AccessDenied, ex);
        }
        catch (AmazonServiceException ex)
        {
            throw new SecretRetrievalException($"AWS Secrets Manager request failed: {ex.Message}", CyberArkErrorCategory.VaultConnectivity, ex);
        }
        catch (Exception ex) when (ex is not SecretRetrievalException)
        {
            // Defensive fallback (D-84) -- AzureKeyVaultSecretsProvider's
            // sibling caught a similar SDK-specific wrapper exception
            // (AggregateException) that didn't match its documented
            // exception types when tested against an unreachable
            // placeholder endpoint; catching broadly here avoids the same
            // class of surprise turning into an unhandled 500.
            throw new SecretRetrievalException($"AWS Secrets Manager request failed: {ex.Message}", CyberArkErrorCategory.VaultConnectivity, ex);
        }
    }

    private AmazonSecretsManagerClient BuildClient(AwsSecretsManagerSettings settings, RegionEndpoint regionEndpoint)
    {
        if (!string.Equals(settings.AuthMethod, "AccessKey", StringComparison.OrdinalIgnoreCase))
        {
            return new AmazonSecretsManagerClient(regionEndpoint);
        }

        if (string.IsNullOrWhiteSpace(settings.AccessKeyId) || string.IsNullOrWhiteSpace(settings.ProtectedCredential))
        {
            throw new SecretRetrievalException(
                "AWS Secrets Manager AccessKey auth requires AccessKeyId and a stored secret access key to be configured.",
                CyberArkErrorCategory.Other);
        }

        string secretAccessKey;
        try
        {
            secretAccessKey = localSecretProtector.Unprotect(settings.ProtectedCredential);
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            throw new SecretRetrievalException(
                "AWS Secrets Manager's stored secret access key could not be decrypted -- it may have been protected on a different machine.",
                CyberArkErrorCategory.Other, ex);
        }

        var credentials = new BasicAWSCredentials(settings.AccessKeyId, secretAccessKey);
        return new AmazonSecretsManagerClient(credentials, regionEndpoint);
    }

    private sealed class AwsSecretsManagerSettings
    {
        public string Region { get; init; } = "";
        public string AuthMethod { get; init; } = "IamRole"; // "IamRole" | "AccessKey"
        public string? AccessKeyId { get; init; }
        public string? ProtectedCredential { get; init; }
    }
}
