using System.Security.Claims;
using System.Text.Encodings.Web;
using BlueTrack.Api.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// Layer 3 (Design_Testing_Strategy.md): simulates DevFakeAuth roles for
/// in-process WebApplicationFactory tests, without a real Negotiate
/// handshake or WindowsIdentity.
///
/// Works because NegotiateProviderResolver and GroupIdentifierExtractor
/// don't actually require a WindowsIdentity for DevFakeAuth -- only that
/// the principal carries the bluetrack:provider_type=DevFakeAuth marker
/// claim (the same one OIDC/SAML stamp on real sign-in) plus
/// Identity.Name matching an identity_group_role_map row (see
/// Database/Test/01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql's
/// TestUser.Viewer / .Analyst / .Approver / .Admin rows). Registered as
/// this test host's default authentication scheme by
/// BlueTrackWebApplicationFactory, so [Authorize]/[Authorize(Policy=...)]
/// exercise the real authorization pipeline end to end.
/// </summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";

    /// <summary>Requests set this header to pick which simulated identity authenticates them; absent means anonymous.</summary>
    public const string TestUserHeaderName = "X-BlueTrack-Test-User";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(TestUserHeaderName, out var username) || string.IsNullOrEmpty(username))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, username.ToString()),
                new Claim(BlueTrackClaimTypes.ProviderType, "DevFakeAuth")
            ],
            authenticationType: SchemeName);

        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
