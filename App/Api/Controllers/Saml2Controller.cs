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
public sealed class Saml2Controller(Saml2ConfigurationFactory configFactory) : ControllerBase
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
        if (!string.IsNullOrEmpty(returnUrl))
        {
            binding.SetRelayStateQuery(new Dictionary<string, string> { [ReturnUrlKey] = returnUrl });
        }

        var request = new Saml2AuthnRequest(config)
        {
            AssertionConsumerServiceUrl = new Uri(Url.ActionLink(nameof(Acs))!)
        };

        return binding.Bind(request).ToActionResult();
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

        var genericRequest = await Request.ToGenericHttpRequestAsync(readBodyAsString: true, validate: true);
        var binding = new Saml2PostBinding();
        var saml2AuthnResponse = new Saml2AuthnResponse(config);

        binding.ReadSamlResponse(genericRequest, saml2AuthnResponse);
        if (saml2AuthnResponse.Status != Saml2StatusCodes.Success)
        {
            // D-26: a rejected assertion is worth surfacing distinctly, not
            // as a generic 500 -- actual audit logging of rejections is a
            // follow-up once a real IdP exists to test against.
            return Problem(
                title: "SAML sign-in was rejected",
                detail: $"IdP status: {saml2AuthnResponse.Status}",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // Validates the signature/conditions (D-26) and populates ClaimsIdentity -- only reached on Status.Success above.
        binding.Unbind(genericRequest, saml2AuthnResponse);

        await saml2AuthnResponse.CreateSession(HttpContext, claimsTransform: principal =>
        {
            (principal.Identity as ClaimsIdentity)?.AddClaim(new Claim(BlueTrackClaimTypes.ProviderType, "SAML"));
            return principal;
        });

        var relayState = binding.GetRelayStateQuery();
        var returnUrl = relayState.TryGetValue(ReturnUrlKey, out var url) && !string.IsNullOrEmpty(url) ? url : "/";
        return Redirect(returnUrl);
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

    [HttpGet("logout")]
    [Authorize]
    public async Task<IActionResult> Logout()
    {
        // D-84 placeholder scope: signs out of this app's own SAML session
        // only. A real SP-initiated Single Logout round trip to the IdP
        // (Saml2LogoutRequest/Response) is a follow-up once a real IdP
        // exists to test the flow against.
        await HttpContext.SignOutAsync(Saml2Constants.AuthenticationScheme);
        return Redirect("/");
    }

    private ObjectResult NotConfigured() => Problem(
        title: "SAML is not configured",
        detail: "Register and enable a SAML row in web.identity_provider_config, with a resolvable IdP certificate, before this endpoint can be used.",
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
