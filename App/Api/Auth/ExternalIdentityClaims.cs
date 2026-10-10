using System.Security.Claims;

namespace BlueTrack.Api.Auth;

/// <summary>
/// D-197: an OIDC or SAML identity provider decides which claims it sends,
/// and BlueTrack must never take its permissions or its provider marker from
/// them -- a "permission" claim in a token would otherwise grant that
/// permission outright (PermissionClaimsTransformation trusted any it found),
/// and a "bluetrack:provider_type" claim could steer NegotiateProviderResolver
/// to another provider. Both are removed before the app adds its own marker.
/// </summary>
public static class ExternalIdentityClaims
{
    private static readonly string[] AppOwnedClaimTypes =
    [
        PermissionClaimsTransformation.PermissionClaimType,
        BlueTrackClaimTypes.ProviderType,
    ];

    /// <summary>Removes app-owned claim types the provider sent, then marks the identity with providerType.</summary>
    public static void MarkProvider(ClaimsPrincipal principal, string providerType)
    {
        foreach (var identity in principal.Identities)
        {
            foreach (var claim in identity.Claims.Where(c => AppOwnedClaimTypes.Contains(c.Type)).ToList())
            {
                identity.TryRemoveClaim(claim);
            }
        }

        (principal.Identity as ClaimsIdentity)?.AddClaim(new Claim(BlueTrackClaimTypes.ProviderType, providerType));
    }
}
