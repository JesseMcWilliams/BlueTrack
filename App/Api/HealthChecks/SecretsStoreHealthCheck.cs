using Microsoft.Extensions.Diagnostics.HealthChecks;
using BlueTrack.Api.Secrets;

namespace BlueTrack.Api.HealthChecks;

/// <summary>
/// D-96 Part 3.2: confirms the active Secrets Store backend resolves to a
/// real provider implementation via the existing VaultSecretProviderResolver
/// -- deliberately dispatch-only, no live secret retrieval (same "stays
/// out of automated scope" stance Design_Testing_Strategy.md already takes
/// for real vaults).
/// </summary>
public sealed class SecretsStoreHealthCheck(VaultSecretProviderResolver resolver) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var provider = await resolver.ResolveActiveAsync();
            return HealthCheckResult.Healthy($"Active backend '{provider.BackendType}' has a provider implementation.");
        }
        catch (SecretRetrievalException ex)
        {
            return HealthCheckResult.Unhealthy(ex.Message);
        }
    }
}
