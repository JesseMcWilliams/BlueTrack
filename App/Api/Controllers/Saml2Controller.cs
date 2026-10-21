using System.Security.Claims;
using ITfoxtec.Identity.Saml2;
using ITfoxtec.Identity.Saml2.MvcCore;
using ITfoxtec.Identity.Saml2.Schemas;
using ITfoxtec.Identity.Saml2.Schemas.Metadata;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// SAML 2.0 SP endpoints (D-84), built against ITfoxtec.Identity.Saml2 --
/// the D-23-selected library, which is action-helper-based rather than an
/// AddAuthentication() scheme like OIDC, following the standard
/// Login/AssertionConsumerService/Metadata/Logout shape from the library's
/// own sample application. Placeholder framework: every action returns a
/// clear 503 if the SAML identity_provider_config row isn't enabled/complete
/// yet (Saml2ConfigurationFactory), matching MeController's established
/// "not configured" pattern rather than crashing. Unverified against a real
/// IdP -- see Saml2ConfigurationFactory's own comment and D-84.
///
/// [AllowAnonymous] is per-action rather than class-level (unlike most
/// anonymous-by-default controllers in this app) because Logout needs
/// [Authorize] -- ASP.NET Core resolves a class-level [AllowAnonymous]
/// before any action-level [Authorize], so it would otherwise silently make
/// Logout anonymous too.
/// </summary>
[ApiController]
[Route("api/auth/saml")]
public sealed class Saml2Controller(Saml2ConfigurationFactory configFactory, SignInRejectionRecorder rejectionRecorder, ILogger<Saml2Controller> logger) : ControllerBase
{
    private const string ReturnUrlKey = "ReturnUrl";

    [HttpGet("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromQuery] string? returnUrl = null)
    {
        var config = await configFactory.BuildAsync();
        if (config is null)
        {
            return NotConfigured();
        }

        var binding = new Saml2RedirectBinding();
        // D-197: only a local path is ever carried through the IdP and back --
        // never another site (an open redirect after a genuine sign-in).
        returnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null;
        if (!string.IsNullOrEmpty(returnUrl))
        {
            binding.SetRelayStateQuery(new Dictionary<string, string> { [ReturnUrlKey] = returnUrl });
        }

        var request = new Saml2AuthnRequest(config)
        {
            AssertionConsumerServiceUrl = new Uri(Url.ActionLink(nameof(Acs))!)
        };

        // D-210: signing the request is where an SP key the App Pool account can't
        // read fails ("Keyset does not exist"); say so rather than an unhandled 500.
        try
        {
            return binding.Bind(request).ToActionResult();
        }
        catch (System.Security.Cryptography.CryptographicException ex)
        {
            logger.LogError(ex, "SAML sign-in request could not be signed with the SP certificate's private key.");
            return Problem(
                title: "SAML sign-in is not available",
                detail: "BlueTrack can't use the SAML SP certificate's private key. The App Pool account needs read access to it "
                    + "(see the Installation guide, \"SAML signing certificate\"). The Identity Providers health check shows the same problem.",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    /// <summary>Assertion Consumer Service -- where the IdP POSTs the SAML response back.</summary>
    [HttpPost("acs")]
    [AllowAnonymous]
    public async Task<IActionResult> Acs()
    {
        var config = await configFactory.BuildAsync();
        if (config is null)
        {
            return NotConfigured();
        }

        // The POST binding reads SAMLResponse from the form; readBodyAsString: true left Form
        // empty and Saml2PostBinding.Read threw (a 500 after every sign-in, found live 2026-10-13).
        var genericRequest = await Request.ToGenericHttpRequestAsync(validate: true);
        var binding = new Saml2PostBinding();
        var saml2AuthnResponse = new Saml2AuthnResponse(config);

        // A response the IdP (or anyone) POSTs here is untrusted input: one that can't be
        // read or doesn't validate is a 400 with no internals, logged in full here.
        try
        {
            binding.ReadSamlResponse(genericRequest, saml2AuthnResponse);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SAML response could not be read.");
            return await RejectedAsync("The identity provider's response could not be read.");
        }
        if (saml2AuthnResponse.Status != Saml2StatusCodes.Success)
        {
            // D-26: a rejected assertion is surfaced distinctly, not as a generic 500; D-285: logged and alerted.
            return await RejectedAsync($"IdP status: {saml2AuthnResponse.Status}");
        }

        // Validates the signature/conditions (D-26) and populates ClaimsIdentity -- only reached on Status.Success above.
        try
        {
            binding.Unbind(genericRequest, saml2AuthnResponse);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SAML response failed validation.");
            return await RejectedAsync("The identity provider's response failed validation (signature, audience, time or replay).");
        }

        await saml2AuthnResponse.CreateSession(HttpContext, claimsTransform: principal =>
        {
            // D-197: any permission/provider claims the IdP sent are dropped first.
            ExternalIdentityClaims.MarkProvider(principal, "SAML");
            return principal;
        });

        var relayState = binding.GetRelayStateQuery();
        var returnUrl = relayState.TryGetValue(ReturnUrlKey, out var url) && Url.IsLocalUrl(url) ? url : "/";
        return LocalRedirect(returnUrl);
    }

    [HttpGet("metadata")]
    [AllowAnonymous]
    public async Task<IActionResult> Metadata()
    {
        var config = await configFactory.BuildAsync();
        if (config is null)
        {
            return NotConfigured();
        }

        var entityDescriptor = new EntityDescriptor(config, signMetadata: false)
        {
            SPSsoDescriptor = new SPSsoDescriptor
            {
                AuthnRequestsSigned = config.SignAuthnRequest,
                WantAssertionsSigned = true,
                AssertionConsumerServices =
                [
                    new AssertionConsumerService
                    {
                        Binding = new Uri("urn:oasis:names:tc:SAML:2.0:bindings:HTTP-POST"),
                        Location = new Uri(Url.ActionLink(nameof(Acs))!),
                        IsDefault = true,
                        Index = 0
                    }
                ],
                SigningCertificates = config.SigningCertificate is not null ? [config.SigningCertificate] : []
            }
        };

        return new Saml2Metadata(entityDescriptor).CreateMetadata().ToActionResult();
    }

    /// <summary>
    /// D-217: SP-initiated Single Logout. With the IdP's logout address set and a
    /// SAML session (it carries the IdP's SessionIndex), BlueTrack's session ends
    /// and the browser goes to the IdP with a (signed) LogoutRequest; the IdP
    /// answers at SingleLogout below. Otherwise only BlueTrack's session ends.
    /// Either way the browser ends on the sign-in page; reason=idle keeps the
    /// idle sign-out's message (D-209).
    /// </summary>
    [HttpGet("logout")]
    [Authorize]
    public async Task<IActionResult> Logout([FromQuery] string? reason = null)
    {
        AuthController.ClearSignInChoice(HttpContext); // D-205
        var config = await configFactory.BuildAsync();
        var saml = await HttpContext.AuthenticateAsync(Saml2Constants.AuthenticationScheme);
        if (config?.SingleLogoutDestination is not null && saml.Succeeded && HasSessionIndex(saml.Principal))
        {
            var binding = new Saml2RedirectBinding();
            binding.SetRelayStateQuery(new Dictionary<string, string> { [ReasonKey] = IsIdle(reason) ? "idle" : "" });
            var logoutRequest = await new Saml2LogoutRequest(config, saml.Principal).DeleteSession(HttpContext);
            return binding.Bind(logoutRequest).ToActionResult();
        }

        await HttpContext.SignOutAsync(Saml2Constants.AuthenticationScheme);
        return Redirect(SignedOutUrl(IsIdle(reason)));
    }

    /// <summary>
    /// D-217: the SP's Single Logout Service (the IdP's "SLS URL"), Redirect or
    /// POST binding. A LogoutResponse finishes an SP-initiated logout; a
    /// LogoutRequest is the IdP signing the user out (front-channel, so the
    /// browser brings BlueTrack's cookie): the session ends and a LogoutResponse
    /// goes back to the IdP. A message that fails validation is logged; for a
    /// response, BlueTrack's session has already ended anyway.
    /// </summary>
    [HttpGet("slo")]
    [HttpPost("slo")]
    [AllowAnonymous]
    public async Task<IActionResult> SingleLogout()
    {
        // Not a SAML message at all: a plain 400, with nothing sent back to the IdP.
        var form = Request.HasFormContentType ? await Request.ReadFormAsync() : null;
        bool Has(string name) => Request.Query.ContainsKey(name) || form?.ContainsKey(name) == true;
        if (!Has("SAMLRequest") && !Has("SAMLResponse"))
        {
            return Problem(title: "Not a SAML logout message", detail: "Expected a SAMLRequest or SAMLResponse.", statusCode: StatusCodes.Status400BadRequest);
        }

        var config = await configFactory.BuildAsync();
        if (config is null)
        {
            return NotConfigured();
        }

        var genericRequest = await Request.ToGenericHttpRequestAsync(validate: true);
        Saml2Binding binding = HttpMethods.IsPost(Request.Method) ? new Saml2PostBinding() : new Saml2RedirectBinding();
        var isResponse = genericRequest.Query?["SAMLResponse"] is not null || genericRequest.Form?["SAMLResponse"] is not null;

        if (isResponse)
        {
            try
            {
                binding.Unbind(genericRequest, new Saml2LogoutResponse(config));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "SAML logout response could not be validated; BlueTrack's session had already ended.");
            }
            await HttpContext.SignOutAsync(Saml2Constants.AuthenticationScheme);
            var idle = binding.GetRelayStateQuery().TryGetValue(ReasonKey, out var reason) && reason == "idle";
            return Redirect(SignedOutUrl(idle));
        }

        var logoutRequest = new Saml2LogoutRequest(config, User);
        Saml2StatusCodes status;
        try
        {
            binding.Unbind(genericRequest, logoutRequest);
            await logoutRequest.DeleteSession(HttpContext);
            AuthController.ClearSignInChoice(HttpContext);
            status = Saml2StatusCodes.Success;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SAML logout request from the identity provider could not be validated.");
            status = Saml2StatusCodes.RequestDenied;
        }

        var responseBinding = new Saml2RedirectBinding { RelayState = binding.RelayState };
        var logoutResponse = new Saml2LogoutResponse(config) { InResponseToAsString = logoutRequest.IdAsString, Status = status };
        return responseBinding.Bind(logoutResponse).ToActionResult();
    }

    private const string ReasonKey = "reason";

    private static bool IsIdle(string? reason) => string.Equals(reason, "idle", StringComparison.Ordinal);

    // The SPA's sign-in page (at the site root, outside this /BlueTrack app), as the user menu's Sign out ends.
    private static string SignedOutUrl(bool idle) => idle ? "/login?signedOut=1&reason=idle" : "/login?signedOut=1";

    public static bool HasSessionIndex(System.Security.Claims.ClaimsPrincipal? principal) =>
        principal?.HasClaim(c => c.Type == ITfoxtec.Identity.Saml2.Claims.Saml2ClaimTypes.SessionIndex) == true;

    /// <summary>D-285: every rejection is audit-logged (LogonFailed) and alerts SignInRejected before the 400 goes out.</summary>
    private async Task<ObjectResult> RejectedAsync(string detail)
    {
        await rejectionRecorder.RecordAsync("SAML", detail, HttpContext.Connection.RemoteIpAddress?.ToString());
        return Problem(title: "SAML sign-in was rejected", detail: detail, statusCode: StatusCodes.Status400BadRequest);
    }

    private ObjectResult NotConfigured() => Problem(
        title: "SAML is not configured",
        detail: "Register and enable a SAML row in web.identity_provider_config, with a resolvable IdP certificate, before this endpoint can be used.",
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
