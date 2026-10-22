using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/admin/identity-providers")]
[Authorize(Policy = Permissions.ManageIdentityProviders)]
public sealed class IdentityProvidersController(
    IdentityProviderRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repository.GetAllAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveIdentityProviderRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var key = await repository.CreateAsync(request);
        await auditLogger.LogAsync("ProviderConfigChanged", user.UserKey, "identity_provider_config", key.ToString(),
            detail: $"Provider '{request.DisplayName}' ({request.ProviderType}) created, enabled={request.IsEnabled}");
        return CreatedAtAction(nameof(GetAll), new { }, new { providerKey = key });
    }

    [HttpPut("{providerKey:int}")]
    public async Task<IActionResult> Update(int providerKey, [FromBody] SaveIdentityProviderRequest request, [FromServices] UserRightsCache rightsCache)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.UpdateAsync(providerKey, request);
        await rightsCache.InvalidateAllAsync();   // D-197: applies to everyone's next request
        await auditLogger.LogAsync("ProviderConfigChanged", user.UserKey, "identity_provider_config", providerKey.ToString(),
            detail: $"Provider '{request.DisplayName}' updated, enabled={request.IsEnabled}");
        return NoContent();
    }

    [HttpDelete("{providerKey:int}")]
    public async Task<IActionResult> Delete(int providerKey, [FromServices] UserRightsCache rightsCache)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.DeleteAsync(providerKey);
        await rightsCache.InvalidateAllAsync();   // D-197: applies to everyone's next request
        await auditLogger.LogAsync("ProviderConfigChanged", user.UserKey, "identity_provider_config", providerKey.ToString(), detail: "Provider deleted");
        return NoContent();
    }

    /// <summary>D-292: RememberLastSignInMethod null (an older client) leaves that setting as it is.</summary>
    public sealed record DefaultSignInProviderBody(string? ProviderType, bool? RememberLastSignInMethod = null);

    /// <summary>
    /// D-205: the default sign-in method as stored (null = none: the sign-in page lists the methods). D-292: and
    /// whether the sign-in page starts the method the browser last signed in with instead.
    /// </summary>
    [HttpGet("default")]
    public async Task<IActionResult> GetDefault() =>
        Ok(new DefaultSignInProviderBody(
            await repository.GetStoredDefaultSignInProviderAsync(),
            await repository.GetRememberLastSignInMethodAsync()));

    [HttpPut("default")]
    public async Task<IActionResult> SetDefault([FromBody] DefaultSignInProviderBody body)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var providerType = string.IsNullOrWhiteSpace(body.ProviderType) ? null : body.ProviderType.Trim();
        if (providerType is not null && !IdentityProviderRepository.SignInProviderTypes.Contains(providerType))
        {
            return Problem(title: "Invalid default sign-in method",
                detail: $"Use one of {string.Join(", ", IdentityProviderRepository.SignInProviderTypes)}, or none.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await repository.SetDefaultSignInProviderAsync(providerType);
        await auditLogger.LogAsync("ProviderConfigChanged", user.UserKey, "app_config", "DefaultSignInProvider",
            detail: $"Default sign-in method set to {providerType ?? "none (sign-in page lists the methods)"}");

        if (body.RememberLastSignInMethod is bool remember && remember != await repository.GetRememberLastSignInMethodAsync())
        {
            await repository.SetRememberLastSignInMethodAsync(remember);
            await auditLogger.LogAsync("ProviderConfigChanged", user.UserKey, "app_config", "RememberLastSignInMethod",
                detail: remember
                    ? "Sign-in page starts the method the browser last signed in with"
                    : "Sign-in page starts the default sign-in method");
        }
        return NoContent();
    }
}
