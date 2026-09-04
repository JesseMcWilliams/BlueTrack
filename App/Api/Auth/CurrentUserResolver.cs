using System.Security.Claims;
using System.Security.Principal;
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
        var externalIdentifier = ResolveExternalIdentifier(principal);
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
        var provider = await negotiateProviderResolver.ResolveAsync();
        if (provider is null)
        {
            return null;
        }

        return await appUserRepository.UpsertOnLoginAsync(
            provider.ProviderKey, externalIdentifier, displayName: principal.Identity?.Name, email: null);
    }

    // web.app_user.ExternalIdentifier is documented as "Windows SID, OIDC
    // sub/object ID, or SAML NameID" -- for both WindowsIntegrated and
    // DevFakeAuth (still real Negotiate underneath) that's the SID off the
    // Windows access token, not the display name (DOMAIN\user).
    private static string? ResolveExternalIdentifier(ClaimsPrincipal principal) =>
        principal.Identity is WindowsIdentity { User: not null } windowsIdentity
            ? windowsIdentity.User.Value
            : principal.Identity?.Name;
}
