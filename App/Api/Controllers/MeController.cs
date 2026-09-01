using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// Demonstrates the Login Flow's step 6 (Design_Authentication_Architecture.md):
/// resolve the normalized identity against web.app_user, upserting on every
/// login. Only Windows Integrated is wired currently (see AuthenticationExtensions) --
/// this controller doesn't yet do the full claims-normalization pipeline
/// (step 5) for OIDC/SAML, since those providers aren't wired yet either.
/// </summary>
[ApiController]
[Route("api/me")]
[Authorize]
public sealed class MeController(
    IdentityProviderRepository providerRepository,
    AppUserRepository appUserRepository) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetCurrentUser()
    {
        var externalIdentifier = User.Identity?.Name;
        if (string.IsNullOrEmpty(externalIdentifier))
        {
            return Unauthorized();
        }

        var provider = await providerRepository.GetByTypeAsync("WindowsIntegrated");
        if (provider is null)
        {
            // identity_provider_config has no WindowsIntegrated row yet -- this
            // is expected until the Identity Providers admin screen (or a seed
            // script) actually registers one. Not an app bug.
            return Problem(
                title: "No WindowsIntegrated provider configured",
                detail: "Register a WindowsIntegrated row in web.identity_provider_config before this endpoint can resolve app_user.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var user = await appUserRepository.UpsertOnLoginAsync(
            provider.ProviderKey,
            externalIdentifier,
            displayName: externalIdentifier,
            email: null);

        return Ok(new
        {
            user.UserKey,
            user.ExternalIdentifier,
            user.DisplayName,
            user.FirstLogin,
            user.LastLogin
        });
    }
}
