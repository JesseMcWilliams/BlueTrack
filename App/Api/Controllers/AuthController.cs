using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
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
public sealed class AuthController(IdentityProviderRepository identityProviderRepository, AuthenticationModeInfo authenticationModeInfo) : ControllerBase
{
    /// <summary>
    /// D-205: set when someone chooses Windows sign-in (the sign-in page, or
    /// /login/windows) while Windows isn't the default. A browser session
    /// cookie, cleared on sign-out.
    /// </summary>
    public const string SignInChoiceCookieName = "BlueTrack.SignIn";
    private const string WindowsChoice = "windows";

    public static void ClearSignInChoice(HttpContext context) =>
        context.Response.Cookies.Delete(SignInChoiceCookieName, SignInChoiceCookieOptions(context));

    private static CookieOptions SignInChoiceCookieOptions(HttpContext context) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "/",
    };

    /// <summary>
    /// For a login screen to render its provider choices (D-41: default
    /// provider first, a small link for the rest) -- deliberately excludes
    /// DevFakeAuth's own admin-only config details, returning only what a
    /// pre-login screen needs. `negotiateDisabled` (D-156) lets the SPA
    /// shell show a banner before a user even signs in -- anonymous by
    /// design, same as the rest of this response, since "this instance has
    /// Windows Integrated Authentication turned off" is exactly the kind
    /// of thing someone should see before they trust the environment at all.
    /// </summary>
    [HttpGet("providers")]
    [AllowAnonymous]
    public async Task<IActionResult> GetEnabledProviders()
    {
        var providers = await identityProviderRepository.GetEnabledAsync();
        return Ok(new
        {
            providers = providers.Select(p => new { p.ProviderType, p.DisplayName, p.DisplayOrder }),
            negotiateDisabled = authenticationModeInfo.NegotiateDisabled,
            // D-205: null = no default; the sign-in page lists the methods.
            defaultProviderType = await identityProviderRepository.GetDefaultSignInProviderAsync()
        });
    }

    /// <summary>
    /// D-205: what the SPA asks before /api/me. Never a 401 -- a 401 here would
    /// make the browser answer IIS's Windows challenge silently, which is what
    /// signed people in with Windows when they came for SSO. `windows` says
    /// whether to try Windows sign-in (/api/me): when it's the default, or
    /// the user chose it. Otherwise an unauthenticated user goes to the
    /// sign-in page, which starts the default SSO or lists the methods.
    /// </summary>
    [HttpGet("session")]
    [AllowAnonymous]
    public async Task<IActionResult> GetSession([FromServices] AppConfigRepository appConfigRepository)
    {
        var defaultProvider = await identityProviderRepository.GetDefaultSignInProviderAsync();
        var chose = Request.Cookies.TryGetValue(SignInChoiceCookieName, out var choice) && choice == WindowsChoice;
        var windowsWanted = !authenticationModeInfo.NegotiateDisabled && (defaultProvider == "WindowsIntegrated" || chose);
        // Found live 2026-10-13: IIS keeps Windows authentication on the TCP connection, so
        // after sign-out the same keep-alive connection still arrives as the Windows user,
        // with no Authorization header. A Windows identity counts here only when Windows is
        // wanted; otherwise the SPA goes to the sign-in page, as it should after sign-out.
        var signedIn = User.Identity?.IsAuthenticated == true
            && (User.Identity is not System.Security.Principal.WindowsIdentity || windowsWanted);
        return Ok(new
        {
            authenticated = signedIn,
            windows = windowsWanted,
            defaultProviderType = defaultProvider,
            // D-209: the SPA signs out after this many minutes without activity.
            idleTimeoutMinutes = (await appConfigRepository.GetAsync()).IdleTimeoutMinutes
        });
    }

    /// <summary>
    /// D-205: the direct address for Windows sign-in (/login/windows in the
    /// SPA lands here). Records the choice, then goes back to the SPA, whose
    /// next /api/me does the Windows sign-in.
    /// </summary>
    [HttpGet("login/windows")]
    [AllowAnonymous]
    public async Task<IActionResult> LoginWindows([FromQuery] string? returnUrl = null)
    {
        var windows = await identityProviderRepository.GetByTypeAsync("WindowsIntegrated");
        if (windows is null || !windows.IsEnabled || authenticationModeInfo.NegotiateDisabled)
        {
            return Problem(title: "Windows sign-in is not enabled", statusCode: StatusCodes.Status404NotFound);
        }

        Response.Cookies.Append(SignInChoiceCookieName, WindowsChoice, SignInChoiceCookieOptions(HttpContext));
        // D-197: local paths only.
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/");
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

        // D-197: local paths only -- the OIDC handler redirects here after sign-in without checking.
        var redirectUri = Url.IsLocalUrl(returnUrl) ? returnUrl! : "/";
        return Challenge(new AuthenticationProperties { RedirectUri = redirectUri }, "OIDC");
    }

    /// <summary>
    /// D-206: the web pages' Sign out. Ends every BlueTrack session the browser
    /// may hold -- the OIDC/DevFakeAuth cookie, the SAML session, and the
    /// Windows choice (D-205). It doesn't sign the user out of the IdP (no
    /// Single Logout yet), and a Windows user is signed straight back in once
    /// they leave the sign-in page while Windows is the default.
    /// </summary>
    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignOutAsync(ITfoxtec.Identity.Saml2.Schemas.Saml2Constants.AuthenticationScheme);
        ClearSignInChoice(HttpContext); // D-205
        return NoContent();
    }
}
