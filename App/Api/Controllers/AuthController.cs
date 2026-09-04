using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-84: login-flow endpoints that don't belong to a specific admin
/// resource. [AllowAnonymous] is per-action rather than class-level since
/// Logout needs [Authorize] -- a class-level [AllowAnonymous] would
/// silently override it (see Saml2Controller's own note on this).
/// </summary>
[ApiController]
[Route("api/auth")]
public sealed class AuthController(IdentityProviderRepository identityProviderRepository) : ControllerBase
{
    /// <summary>
    /// For a login screen to render its provider choices (D-41: default
    /// provider first, a small link for the rest) -- deliberately excludes
    /// DevFakeAuth's own admin-only config details, returning only what a
    /// pre-login screen needs.
    /// </summary>
    [HttpGet("providers")]
    [AllowAnonymous]
    public async Task<IActionResult> GetEnabledProviders()
    {
        var providers = await identityProviderRepository.GetEnabledAsync();
        return Ok(providers.Select(p => new { p.ProviderType, p.DisplayName, p.DisplayOrder }));
    }

    /// <summary>
    /// Issues an OIDC challenge -- a 404 here (rather than a 503 like
    /// Saml2Controller's pattern) means the OIDC scheme was never
    /// registered at startup, which AuthenticationExtensions.cs's own
    /// comment explains only happens when it isn't configured/enabled.
    /// </summary>
    [HttpGet("login/oidc")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginOidc([FromQuery] string? returnUrl = null)
    {
        var schemes = HttpContext.RequestServices.GetRequiredService<IAuthenticationSchemeProvider>();
        if (await schemes.GetSchemeAsync("OIDC") is null)
        {
            return Problem(
                title: "OIDC is not configured",
                detail: "Register and enable an OIDC row in web.identity_provider_config with a stored client secret, then restart the app -- OIDC scheme registration happens once at startup.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var redirectUri = string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl;
        return Challenge(new AuthenticationProperties { RedirectUri = redirectUri }, "OIDC");
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return NoContent();
    }
}
