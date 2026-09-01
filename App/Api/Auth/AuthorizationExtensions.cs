using Microsoft.AspNetCore.Authentication;

namespace BlueTrack.Api.Auth;

/// <summary>
/// Registers one ASP.NET Core authorization policy per permission in the
/// confirmed catalog (web.app_permission, D-61) -- [Authorize(Policy =
/// Permissions.X)] then just checks for the "permission" claim
/// PermissionClaimsTransformation adds, never touching role/group lookup
/// logic directly.
/// </summary>
public static class AuthorizationExtensions
{
    public static IServiceCollection AddBlueTrackAuthorization(this IServiceCollection services)
    {
        var authorizationBuilder = services.AddAuthorizationBuilder();

        foreach (var permission in Permissions.All)
        {
            authorizationBuilder.AddPolicy(permission, policy =>
                policy.RequireClaim(PermissionClaimsTransformation.PermissionClaimType, permission));
        }

        services.AddTransient<IClaimsTransformation, PermissionClaimsTransformation>();

        return services;
    }
}
