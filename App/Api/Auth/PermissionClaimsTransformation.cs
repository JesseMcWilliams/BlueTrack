using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace BlueTrack.Api.Auth;

/// <summary>
/// Adds one "permission" claim per effective permission (UserRightsResolver)
/// to every authenticated request, so ASP.NET Core policies
/// (AuthorizationExtensions.AddBlueTrackAuthorization) can just check for a
/// claim instead of touching group/role lookup logic directly.
///
/// D-13/D-82: UserRightsResolver.ResolveAsync is cache-first (per identity,
/// via UserRightsCache/web.distributed_cache) -- this transformation itself
/// doesn't need to know or care whether a given call was a cache hit or a
/// live re-resolution, only that it gets an up-to-date UserRights back.
/// </summary>
public sealed class PermissionClaimsTransformation(UserRightsResolver rightsResolver) : IClaimsTransformation
{
    public const string PermissionClaimType = "permission";

    // D-197: the identity this transformation adds is tagged with this
    // authentication type, so "already resolved" means "our identity is
    // present" -- not "some permission claim is present", which a token from
    // an identity provider could have supplied.
    private const string PermissionIdentityType = "BlueTrack.Permissions";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not { IsAuthenticated: true })
        {
            return principal;
        }

        // IClaimsTransformation can run more than once per request -- don't
        // re-query or add duplicate claims if it already has.
        if (principal.Identities.Any(i => i.AuthenticationType == PermissionIdentityType))
        {
            return principal;
        }

        // Permissions come only from BlueTrack's own role mapping: drop any
        // permission claim another identity carries (D-197).
        foreach (var other in principal.Identities)
        {
            foreach (var claim in other.Claims.Where(c => c.Type == PermissionClaimType).ToList())
            {
                other.TryRemoveClaim(claim);
            }
        }

        var rights = await rightsResolver.ResolveAsync(principal);
        if (rights.PermissionNames.Count == 0)
        {
            return principal;
        }

        var identity = new ClaimsIdentity(PermissionIdentityType);
        foreach (var permissionName in rights.PermissionNames)
        {
            identity.AddClaim(new Claim(PermissionClaimType, permissionName));
        }

        principal.AddIdentity(identity);
        return principal;
    }
}
