using System.Security.Claims;
using System.Security.Principal;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Auth;

/// <summary>
/// Resolves which web.identity_provider_config row actually authenticated
/// the current request. Originally Negotiate-only (hence the class name,
/// kept to avoid an unnecessary rename/DI-registration churn across the
/// three call sites) -- extended for D-84's OIDC/SAML placeholder framework
/// to accept the ClaimsPrincipal and branch:
///
/// - A WindowsIdentity means Negotiate authenticated it (the only scheme
///   that produces one) -- Windows Integrated vs. DevFakeAuth is then
///   exactly the original logic: DevFakeAuth only takes effect in
///   Development, enforced in code (not trusted from its own IsEnabled
///   toggle alone).
/// - Otherwise, look for the BlueTrackClaimTypes.ProviderType marker claim
///   stamped onto the identity during OIDC/SAML sign-in (there's no other
///   reliable way to tell "which provider" once more than one non-Negotiate
///   scheme can be registered -- unlike Negotiate, OIDC/SAML don't produce
///   a distinctly-typed ClaimsIdentity to branch on).
/// - No WindowsIdentity and no marker claim means an unrecognized/anonymous
///   principal -- returns null, same as before.
/// </summary>
public sealed class NegotiateProviderResolver(
    IdentityProviderRepository identityProviderRepository,
    IHostEnvironment hostEnvironment)
{
    public async Task<IdentityProviderConfig?> ResolveAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is WindowsIdentity)
        {
            if (hostEnvironment.IsDevelopment())
            {
                var devFakeAuth = await identityProviderRepository.GetByTypeAsync("DevFakeAuth");
                if (devFakeAuth is { IsEnabled: true })
                {
                    return devFakeAuth;
                }
            }

            return await identityProviderRepository.GetByTypeAsync("WindowsIntegrated");
        }

        var providerType = principal.FindFirst(BlueTrackClaimTypes.ProviderType)?.Value;
        return string.IsNullOrEmpty(providerType)
            ? null
            : await identityProviderRepository.GetByTypeAsync(providerType);
    }
}
