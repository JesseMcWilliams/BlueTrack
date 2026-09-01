using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;

namespace BlueTrack.Api.Auth;

/// <summary>
/// Adds one "permission" claim per effective permission (UserRightsResolver)
/// to every authenticated request, so ASP.NET Core policies
/// (AuthorizationExtensions.AddBlueTrackAuthorization) can just check for a
/// claim instead of touching group/role lookup logic directly.
///
/// KNOWN GAP: the design calls for permissions to be resolved once (at
/// login) and cached for the session, refreshed only by an explicit Reload
/// Rights action (D-13/D-14) -- not re-evaluated on every single request,
/// for performance. There's no session/cookie layer in this scaffold yet
/// (Negotiate alone doesn't provide one), so this transformation currently
/// re-resolves on every request instead. Still functionally correct -- it's
/// the same live query Reload Rights itself would run -- just not yet the
/// cheaper cached version the design calls for. Add a session cache once
/// this app actually has a session store.
/// </summary>
public sealed class PermissionClaimsTransformation(UserRightsResolver rightsResolver) : IClaimsTransformation
{
    public const string PermissionClaimType = "permission";

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not { IsAuthenticated: true })
        {
            return principal;
        }

        // IClaimsTransformation can run more than once per request -- don't
        // re-query or add duplicate claims if it already has.
        if (principal.HasClaim(c => c.Type == PermissionClaimType))
        {
            return principal;
        }

        var rights = await rightsResolver.ResolveAsync(principal);
        if (rights.PermissionNames.Count == 0)
        {
            return principal;
        }

        var identity = new ClaimsIdentity();
        foreach (var permissionName in rights.PermissionNames)
        {
            identity.AddClaim(new Claim(PermissionClaimType, permissionName));
        }

        principal.AddIdentity(identity);
        return principal;
    }
}
