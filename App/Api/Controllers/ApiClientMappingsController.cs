using BlueTrack.Api.ApiAccess;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-243 (phase 2): Admin > API Keys > API client mappings. Which role a caller gets when it exchanges a token
/// from an OIDC identity provider (ApiTokenController): by the token's client, or by a value in its groups claim.
/// </summary>
[ApiController]
[Route("api/admin/api-client-mappings")]
[Authorize(Policy = Permissions.ManageApiKeys)]
public sealed class ApiClientMappingsController(
    ApiClientMappingRepository mappings,
    ApiKeyRepository keys,
    IdentityProviderRepository providers,
    RoleRepository roles,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger,
    IMemoryCache cache) : ControllerBase
{
    public static readonly string[] MatchTypes = ["ClientId", "Group"];

    public sealed record CreateMappingRequest(int ProviderKey, string? MatchType, string? MatchValue, int AppRoleKey, string? DisplayName, int? DisplayOrder);

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await mappings.ListAsync());

    /// <summary>The enabled OIDC identity providers, the roles a mapping may give (every role but Admin) and the match types.</summary>
    [HttpGet("options")]
    public async Task<IActionResult> GetOptions() => Ok(new
    {
        Providers = (await providers.GetAllConfigsAsync()).Where(p => p.ProviderType == "OIDC" && p.IsEnabled).Select(p => new { p.ProviderKey, p.DisplayName }),
        Roles = (await roles.GetRolesAsync()).Where(r => r.RoleName != ApiAccessRules.AdminRoleName).Select(r => new { r.AppRoleKey, r.RoleName }),
        MatchTypes,
    });

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateMappingRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var provider = await providers.GetByKeyAsync(request.ProviderKey);
        if (provider is not { ProviderType: "OIDC" }) return BadRequest(new { error = "Choose an OIDC identity provider." });
        var matchType = MatchTypes.FirstOrDefault(t => string.Equals(t, request.MatchType, StringComparison.OrdinalIgnoreCase));
        if (matchType is null) return BadRequest(new { error = "Match must be ClientId or Group." });
        var value = request.MatchValue?.Trim();
        if (string.IsNullOrEmpty(value) || value.Length > 300) return BadRequest(new { error = "The client ID or group is required (300 characters at most)." });
        var roleName = await keys.GetRoleNameAsync(request.AppRoleKey);
        if (roleName is null) return BadRequest(new { error = "That role does not exist." });
        if (roleName == ApiAccessRules.AdminRoleName) return BadRequest(new { error = "An API caller can't hold the Admin role. Choose a narrower role." });
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? null : request.DisplayName.Trim();
        if (displayName is { Length: > 100 }) return BadRequest(new { error = "The name is 100 characters at most." });
        var order = request.DisplayOrder ?? 100;
        if (order is < 0 or > 10000) return BadRequest(new { error = "Order must be 0 to 10000." });
        if (await mappings.ExistsAsync(request.ProviderKey, matchType, value)) return Conflict(new { error = $"There is already a mapping for {matchType} '{value}' on {provider.DisplayName}." });

        var key = await mappings.CreateAsync(request.ProviderKey, matchType, value, request.AppRoleKey, displayName, order, user.UserKey);
        await auditLogger.LogAsync("ApiClientMappingChanged", user.UserKey, "api_client_mapping", key.ToString(),
            detail: $"Mapping created: {provider.DisplayName}, {matchType} '{value}' gets role {roleName} (order {order})");
        return StatusCode(StatusCodes.Status201Created, new { apiClientMappingKey = key });
    }

    [HttpPost("{apiClientMappingKey:int}/enable")]
    public Task<IActionResult> Enable(int apiClientMappingKey) => SetEnabledAsync(apiClientMappingKey, true);

    [HttpPost("{apiClientMappingKey:int}/disable")]
    public Task<IActionResult> Disable(int apiClientMappingKey) => SetEnabledAsync(apiClientMappingKey, false);

    [HttpDelete("{apiClientMappingKey:int}")]
    public async Task<IActionResult> Delete(int apiClientMappingKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var existing = await mappings.GetAsync(apiClientMappingKey);
        if (existing is null || !await mappings.DeleteAsync(apiClientMappingKey)) return NotFound();
        cache.Remove($"{ApiTokenAuthenticationMiddleware.MappingStateCachePrefix}{apiClientMappingKey}");
        await auditLogger.LogAsync("ApiClientMappingChanged", user.UserKey, "api_client_mapping", apiClientMappingKey.ToString(),
            detail: $"Mapping deleted: {existing.ProviderName}, {existing.MatchType} '{existing.MatchValue}' (role {existing.RoleName})");
        return NoContent();
    }

    private async Task<IActionResult> SetEnabledAsync(int apiClientMappingKey, bool isEnabled)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var existing = await mappings.GetAsync(apiClientMappingKey);
        if (existing is null || !await mappings.SetEnabledAsync(apiClientMappingKey, isEnabled)) return NotFound();
        cache.Remove($"{ApiTokenAuthenticationMiddleware.MappingStateCachePrefix}{apiClientMappingKey}");
        await auditLogger.LogAsync("ApiClientMappingChanged", user.UserKey, "api_client_mapping", apiClientMappingKey.ToString(),
            detail: $"Mapping {(isEnabled ? "enabled" : "disabled")}: {existing.ProviderName}, {existing.MatchType} '{existing.MatchValue}' (role {existing.RoleName})");
        return NoContent();
    }
}
