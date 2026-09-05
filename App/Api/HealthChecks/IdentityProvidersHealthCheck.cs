using Microsoft.Extensions.Diagnostics.HealthChecks;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.HealthChecks;

/// <summary>
/// D-96 Part 3.2: confirms at least one identity provider is enabled and,
/// for OIDC/SAML specifically, that its required settings fields are
/// actually populated -- deliberately NOT a live IdP reachability check
/// (reaching a real IdP over the network stays manually verified, same as
/// this project's existing stance elsewhere).
/// </summary>
public sealed class IdentityProvidersHealthCheck(IdentityProviderRepository repository) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var providers = await repository.GetAllAsync();
        var enabled = providers.Where(p => p.IsEnabled).ToList();
        if (enabled.Count == 0)
        {
            return HealthCheckResult.Unhealthy("No identity provider is enabled.");
        }

        var problems = new List<string>();
        foreach (var provider in enabled)
        {
            if (provider.ProviderType == "OIDC")
            {
                var settings = ProviderSettingsReader.ReadOidc(provider.ConfigurationValues);
                if (settings is null || string.IsNullOrWhiteSpace(settings.Authority) || string.IsNullOrWhiteSpace(settings.ClientId))
                {
                    problems.Add($"{provider.DisplayName} (OIDC) is missing Authority/ClientId.");
                }
            }
            else if (provider.ProviderType == "SAML")
            {
                var settings = ProviderSettingsReader.ReadSaml(provider.ConfigurationValues);
                if (settings is null || string.IsNullOrWhiteSpace(settings.SpEntityId) || string.IsNullOrWhiteSpace(settings.IdpEntityId)
                    || string.IsNullOrWhiteSpace(settings.IdpSingleSignOnDestination))
                {
                    problems.Add($"{provider.DisplayName} (SAML) is missing required fields.");
                }
            }
        }

        return problems.Count == 0
            ? HealthCheckResult.Healthy($"{enabled.Count} provider(s) enabled and populated.")
            : HealthCheckResult.Degraded(string.Join(' ', problems));
    }
}
