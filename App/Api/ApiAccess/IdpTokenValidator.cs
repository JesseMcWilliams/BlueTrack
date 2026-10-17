using System.Collections.Concurrent;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace BlueTrack.Api.ApiAccess;

/// <summary>An identity provider's issuer and the keys its tokens are signed with.</summary>
public sealed record IdpMetadata(string Issuer, IReadOnlyCollection<SecurityKey> SigningKeys);

/// <summary>Where the issuer and signing keys of an OIDC authority come from (a seam, so tests need no network).</summary>
public interface IIdpMetadataSource
{
    Task<IdpMetadata> GetAsync(string authority, CancellationToken cancellationToken = default);
}

/// <summary>
/// The authority's own OpenID Connect discovery document (…/.well-known/openid-configuration, https
/// only), cached and refreshed by the IdentityModel configuration manager. The authority is the one an
/// admin entered on Admin > Identity Providers, never an address taken from a token.
/// </summary>
public sealed class OidcIdpMetadataSource : IIdpMetadataSource
{
    private readonly ConcurrentDictionary<string, ConfigurationManager<OpenIdConnectConfiguration>> _managers = new();

    public async Task<IdpMetadata> GetAsync(string authority, CancellationToken cancellationToken = default)
    {
        var address = authority.TrimEnd('/') + "/.well-known/openid-configuration";
        var manager = _managers.GetOrAdd(address, a =>
            new ConfigurationManager<OpenIdConnectConfiguration>(a, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = true }));
        var configuration = await manager.GetConfigurationAsync(cancellationToken);
        return new IdpMetadata(configuration.Issuer, configuration.SigningKeys.ToList());
    }
}

/// <summary>What a validated identity provider token says about its caller.</summary>
public sealed record IdpCaller(int ProviderKey, string ProviderName, string Subject, string? ClientId, IReadOnlyList<string> Groups, string DisplayName);

/// <summary>
/// D-243 (phase 2): checks a token an OIDC identity provider issued (its signature against the keys the
/// provider publishes, its issuer, that it hasn't expired), for the token exchange. Only providers an admin
/// enabled on Identity Providers are trusted, and only with asymmetric signatures (a symmetric one could be
/// forged by anyone who knows a public value). The audience isn't checked: a script's own application is
/// the token's audience, not BlueTrack; what the caller may do comes only from an API client mapping.
/// </summary>
public sealed class IdpTokenValidator(IdentityProviderRepository providers, IIdpMetadataSource metadataSource, TimeProvider timeProvider, ILogger<IdpTokenValidator> logger)
{
    private static readonly string[] Algorithms =
    [
        SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
        SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512,
        SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
    ];

    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(60);

    public async Task<IdpCaller?> ValidateAsync(string token)
    {
        string? issuer;
        try
        {
            issuer = new JsonWebTokenHandler().ReadJsonWebToken(token).Issuer;
        }
        catch (ArgumentException)
        {
            return null;
        }
        if (string.IsNullOrEmpty(issuer)) return null;

        foreach (var provider in await providers.GetAllConfigsAsync())
        {
            if (provider.ProviderType != "OIDC" || !provider.IsEnabled) continue;
            var settings = ProviderSettingsReader.ReadOidc(provider.ConfigurationValues);
            if (settings is null || string.IsNullOrWhiteSpace(settings.Authority)) continue;

            IdpMetadata metadata;
            try
            {
                metadata = await metadataSource.GetAsync(settings.Authority);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not read the OpenID Connect metadata of identity provider {Provider} for an API token request.", provider.DisplayName);
                continue;
            }
            if (!string.Equals(metadata.Issuer, issuer, StringComparison.Ordinal)) continue;

            var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
            {
                ValidIssuer = metadata.Issuer,
                IssuerSigningKeys = metadata.SigningKeys,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                ValidAlgorithms = Algorithms,
                ValidateAudience = false,
                RequireExpirationTime = true,
                ValidateLifetime = true,
                ClockSkew = ClockSkew,
                LifetimeValidator = (notBefore, expires, _, _) =>
                    expires is not null && expires.Value.ToUniversalTime() > timeProvider.GetUtcNow().UtcDateTime - ClockSkew
                    && (notBefore is null || notBefore.Value.ToUniversalTime() <= timeProvider.GetUtcNow().UtcDateTime + ClockSkew),
            });
            if (!result.IsValid) return null;

            var identity = result.ClaimsIdentity;
            var subject = identity.FindFirst("sub")?.Value;
            if (string.IsNullOrEmpty(subject)) return null;
            var clientId = new[] { "azp", "client_id", "cid" }.Select(type => identity.FindFirst(type)?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            var groupClaim = settings.GroupsClaimType;
            var groups = string.IsNullOrWhiteSpace(groupClaim)
                ? []
                : identity.FindAll(groupClaim).Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Distinct(StringComparer.Ordinal).ToList();
            var name = new[] { "preferred_username", "name" }.Select(type => identity.FindFirst(type)?.Value).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? clientId ?? subject;
            return new IdpCaller(provider.ProviderKey, provider.DisplayName, subject, clientId, groups, name);
        }
        return null;
    }
}
