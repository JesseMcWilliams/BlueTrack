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
    public async Task<IActionResult> Update(int providerKey, [FromBody] SaveIdentityProviderRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.UpdateAsync(providerKey, request);
        await auditLogger.LogAsync("ProviderConfigChanged", user.UserKey, "identity_provider_config", providerKey.ToString(),
            detail: $"Provider '{request.DisplayName}' updated, enabled={request.IsEnabled}");
        return NoContent();
    }

    [HttpDelete("{providerKey:int}")]
    public async Task<IActionResult> Delete(int providerKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.DeleteAsync(providerKey);
        await auditLogger.LogAsync("ProviderConfigChanged", user.UserKey, "identity_provider_config", providerKey.ToString(), detail: "Provider deleted");
        return NoContent();
    }
}
