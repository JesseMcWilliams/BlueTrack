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
    IdentityProviderRepository identityProviderRepository,
    AppUserRepository appUserRepository)
{
    public async Task<AppUser?> ResolveAsync(ClaimsPrincipal principal)
    {
        var externalIdentifier = ResolveExternalIdentifier(principal);
        if (string.IsNullOrEmpty(externalIdentifier))
        {
            return null;
        }

        // Only WindowsIntegrated is wired (AuthenticationExtensions.cs) --
        // once OIDC/SAML are added, the provider needs to come from the
        // scheme that actually authenticated this request.
        var provider = await identityProviderRepository.GetByTypeAsync("WindowsIntegrated");
        if (provider is null)
        {
            return null;
        }

        return await appUserRepository.UpsertOnLoginAsync(
            provider.ProviderKey, externalIdentifier, displayName: principal.Identity?.Name, email: null);
    }

    // web.app_user.ExternalIdentifier is documented as "Windows SID, OIDC
    // sub/object ID, or SAML NameID" -- for WindowsIntegrated that's the SID
    // off the Windows access token, not the display name (DOMAIN\user).
    private static string? ResolveExternalIdentifier(ClaimsPrincipal principal) =>
        principal.Identity is WindowsIdentity { User: not null } windowsIdentity
            ? windowsIdentity.User.Value
            : principal.Identity?.Name;
}
