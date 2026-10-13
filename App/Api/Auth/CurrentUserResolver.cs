using System.Security.Claims;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Auth;

/// <summary>
/// Resolves the current request's web.app_user row (upsert-on-login, Login
/// Flow step 6, D-59) -- shared by MeController's own /api/me endpoint and
/// any other controller that needs the current user's UserKey (e.g.
/// risk_exception.ApprovedBy), not just MeController.
/// </summary>
public sealed class CurrentUserResolver(
    NegotiateProviderResolver negotiateProviderResolver,
    AppUserRepository appUserRepository)
{
    public async Task<AppUser?> ResolveAsync(ClaimsPrincipal principal)
    {
        var externalIdentifier = ExternalIdentifierReader.Resolve(principal);
        if (string.IsNullOrEmpty(externalIdentifier))
        {
            return null;
        }

        // WindowsIntegrated vs. DevFakeAuth (both Negotiate) is resolved by
        // NegotiateProviderResolver -- a person who authenticates the same
        // way but under DevFakeAuth's substituted group mapping gets a
        // distinct app_user row from their WindowsIntegrated one (same
        // ExternalIdentifier, different ProviderKey), consistent with
        // UQ_app_user's (ProviderKey, ExternalIdentifier) uniqueness.
        var provider = await negotiateProviderResolver.ResolveAsync(principal);
        if (provider is null)
        {
            return null;
        }

        var displayName = DisplayNameFor(principal, externalIdentifier);

        return await appUserRepository.UpsertOnLoginAsync(
            provider.ProviderKey, externalIdentifier, displayName, email: principal.FindFirst(ClaimTypes.Email)?.Value);
    }

    /// <summary>
    /// For WindowsIdentity, Identity.Name is a friendly DOMAIN\user name.
    /// For OIDC/SAML, Identity.Name deliberately holds the raw sub/NameID
    /// (ExternalIdentifierReader's contract) rather than a friendly name, so a
    /// friendlier claim is preferred if the provider supplied one -- falling
    /// back to the identifier itself. Blank claims are skipped: Authentik sends
    /// "name": "" for a user with no full name, which left the name empty
    /// (found live 2026-10-13).
    /// </summary>
    public static string DisplayNameFor(ClaimsPrincipal principal, string externalIdentifier)
    {
        if (principal.Identity is System.Security.Principal.WindowsIdentity && !string.IsNullOrWhiteSpace(principal.Identity.Name))
        {
            return principal.Identity.Name;
        }
        return new[] { "name", ClaimTypes.GivenName, "preferred_username" }
            .Select(type => principal.FindFirst(type)?.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
            ?? externalIdentifier;
    }
}
