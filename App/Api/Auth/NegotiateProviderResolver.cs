using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Auth;

/// <summary>
/// Windows Integrated and DevFakeAuth both authenticate through the exact
/// same ASP.NET Core Negotiate handler -- Design_Authentication_Architecture.md's
/// own point is that "DevFakeAuth is therefore not a separate authentication
/// mechanism." What differs is which identity_group_role_map rows apply to
/// the result. This decides that, enforcing DevFakeAuth's guard condition
/// in code ("can only be enabled when the application's hosting environment
/// is Development") rather than trusting its own IsEnabled admin toggle
/// alone -- a DevFakeAuth row left enabled by mistake still can't take
/// effect outside Development.
/// </summary>
public sealed class NegotiateProviderResolver(
    IdentityProviderRepository identityProviderRepository,
    IHostEnvironment hostEnvironment)
{
    public async Task<IdentityProviderConfig?> ResolveAsync()
    {
        if (hostEnvironment.IsDevelopment())
        {
            var devFakeAuth = await identityProviderRepository.GetByTypeAsync("DevFakeAuth");
            if (devFakeAuth is { IsEnabled: true })
            {
                return devFakeAuth;
            }
        }

        return await identityProviderRepository.GetByTypeAsync("WindowsIntegrated");
    }
}
