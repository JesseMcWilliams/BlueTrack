using System.Security.Claims;
using System.Text.RegularExpressions;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// Test-only sign-in for Playwright E2E tests (Design_Testing_Strategy.md
/// layer 4) -- lets a real browser switch between simulated DevFakeAuth
/// roles (TestUser.Viewer / .Analyst / .Approver / .Admin, seeded by
/// Database/Test/01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql) without a
/// real Negotiate handshake, which can only ever authenticate as the one
/// Windows account the browser/CI runner actually runs as.
///
/// Signs into the same Cookie scheme OIDC/SAML already use
/// (AuthenticationExtensions.cs's SignInScheme, adopted by Program.cs's
/// cookie-fallback middleware when Negotiate didn't authenticate the
/// request) with the same bluetrack:provider_type=DevFakeAuth marker claim
/// NegotiateProviderResolver already recognizes -- no new authentication
/// pathway, just a test-only way to reach the existing one without a real
/// Windows login.
///
/// Guarded in code, not just [AllowAnonymous]: returns 404 outside
/// Development (same guard NegotiateProviderResolver applies to
/// DevFakeAuth itself), and only accepts usernames matching the TestUser.*
/// convention -- never an arbitrary identity, even in Development.
/// </summary>
[ApiController]
[Route("api/auth/dev")]
public sealed class DevTestAuthController(
    IdentityProviderRepository identityProviderRepository,
    IHostEnvironment hostEnvironment) : ControllerBase
{
    private static readonly Regex TestUsernamePattern = new("^TestUser\\.[A-Za-z0-9]+$");

    [HttpGet("test-signin")]
    [AllowAnonymous]
    public async Task<IActionResult> TestSignIn([FromQuery] string username, [FromQuery] string? returnUrl = null)
    {
        if (!hostEnvironment.IsDevelopment())
        {
            return NotFound();
        }

        if (string.IsNullOrEmpty(username) || !TestUsernamePattern.IsMatch(username))
        {
            return Problem(
                title: "Invalid test username",
                detail: "username must match TestUser.<Role> (e.g. TestUser.Approver), matching Database/Test/01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var devFakeAuth = await identityProviderRepository.GetByTypeAsync("DevFakeAuth");
        if (devFakeAuth is not { IsEnabled: true })
        {
            return NotFound();
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, username),
                new Claim(BlueTrackClaimTypes.ProviderType, "DevFakeAuth")
            ],
            authenticationType: "DevFakeAuthTest");

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));

        return string.IsNullOrEmpty(returnUrl) ? NoContent() : Redirect(returnUrl);
    }
}
