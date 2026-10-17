using BlueTrack.Api.ApiAccess;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-243: Admin > API Keys. Creates, lists, disables and deletes the keys
/// scripts and other systems exchange for tokens (ApiTokenController). The
/// key itself is shown once, in the response to the create, and never again:
/// only its hash is stored.
/// </summary>
[ApiController]
[Route("api/admin/api-keys")]
[Authorize(Policy = Permissions.ManageApiKeys)]
public sealed class ApiKeysController(
    ApiKeyRepository keys,
    RoleRepository roles,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger,
    IMemoryCache cache,
    TimeProvider timeProvider) : ControllerBase
{
    public sealed record CreateApiKeyRequest(string? DisplayName, int AppRoleKey, int? LifetimeDays);

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await keys.ListAsync());

    /// <summary>The roles a key may hold (every role but Admin), the default lifetime and the longest allowed.</summary>
    [HttpGet("options")]
    public async Task<IActionResult> GetOptions() => Ok(new
    {
        Roles = (await roles.GetRolesAsync()).Where(r => r.RoleName != ApiAccessRules.AdminRoleName).Select(r => new { r.AppRoleKey, r.RoleName }),
        DefaultLifetimeDays = await keys.GetDefaultLifetimeDaysAsync(),
        MaxLifetimeDays = ApiAccessRules.MaxLifetimeDays,
    });

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApiKeyRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var name = request.DisplayName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) return BadRequest(new { error = "Name is required (100 characters at most)." });
        var roleName = await keys.GetRoleNameAsync(request.AppRoleKey);
        if (roleName is null) return BadRequest(new { error = "That role does not exist." });
        if (roleName == ApiAccessRules.AdminRoleName) return BadRequest(new { error = "An API key can't hold the Admin role. Choose a narrower role." });

        var days = request.LifetimeDays ?? await keys.GetDefaultLifetimeDaysAsync();
        if (days < 1 || days > ApiAccessRules.MaxLifetimeDays) return BadRequest(new { error = $"Lifetime must be 1 to {ApiAccessRules.MaxLifetimeDays} days." });
        if (await keys.NameExistsAsync(name)) return Conflict(new { error = $"There is already an API key named '{name}'." });

        var (key, prefix, hash) = ApiKeyGenerator.Create();
        var expiresAt = timeProvider.GetUtcNow().UtcDateTime.AddDays(days);
        var apiKeyKey = await keys.CreateAsync(name, prefix, hash, request.AppRoleKey, expiresAt, user.UserKey);
        await auditLogger.LogAsync("ApiKeyCreated", user.UserKey, "api_key", apiKeyKey.ToString(),
            detail: $"API key '{name}' ({prefix}) created with role {roleName}, expires {expiresAt:yyyy-MM-dd}");
        return StatusCode(StatusCodes.Status201Created, new { apiKeyKey, apiKey = key, keyPrefix = prefix, expiresAt });
    }

    /// <summary>
    /// A new key with the same role as this one, for switching a caller over: the old key
    /// stays enabled until the admin disables or deletes it. Shown once, like any new key.
    /// </summary>
    [HttpPost("{apiKeyKey:int}/rotate")]
    public async Task<IActionResult> Rotate(int apiKeyKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var existing = await keys.GetAsync(apiKeyKey);
        if (existing is null) return NotFound();

        var today = timeProvider.GetUtcNow().UtcDateTime;
        var baseName = $"{existing.DisplayName} (rotated {today:yyyy-MM-dd})";
        if (baseName.Length > 100) baseName = $"{existing.DisplayName[..(100 - " (rotated yyyy-MM-dd)".Length)]} (rotated {today:yyyy-MM-dd})";
        var name = baseName;
        for (var n = 2; await keys.NameExistsAsync(name); n++)
        {
            name = $"{baseName} {n}";
            if (n > 50) return Conflict(new { error = "Too many keys were rotated from this one today; delete some first." });
        }

        var (key, prefix, hash) = ApiKeyGenerator.Create();
        var expiresAt = today.AddDays(await keys.GetDefaultLifetimeDaysAsync());
        var newKey = await keys.CreateAsync(name, prefix, hash, existing.AppRoleKey, expiresAt, user.UserKey);
        await auditLogger.LogAsync("ApiKeyCreated", user.UserKey, "api_key", newKey.ToString(),
            detail: $"API key '{name}' ({prefix}) created with role {existing.RoleName}, expires {expiresAt:yyyy-MM-dd}, rotated from '{existing.DisplayName}' ({existing.KeyPrefix})");
        return StatusCode(StatusCodes.Status201Created, new { apiKeyKey = newKey, apiKey = key, keyPrefix = prefix, expiresAt, displayName = name, rotatedFrom = existing.DisplayName });
    }

    [HttpPost("{apiKeyKey:int}/enable")]
    public Task<IActionResult> Enable(int apiKeyKey) => SetEnabledAsync(apiKeyKey, true);

    [HttpPost("{apiKeyKey:int}/disable")]
    public Task<IActionResult> Disable(int apiKeyKey) => SetEnabledAsync(apiKeyKey, false);

    [HttpDelete("{apiKeyKey:int}")]
    public async Task<IActionResult> Delete(int apiKeyKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var existing = await keys.GetAsync(apiKeyKey);
        if (existing is null || !await keys.DeleteAsync(apiKeyKey)) return NotFound();
        cache.Remove($"apikey-state:{apiKeyKey}");
        await auditLogger.LogAsync("ApiKeyChanged", user.UserKey, "api_key", apiKeyKey.ToString(),
            detail: $"API key '{existing.DisplayName}' ({existing.KeyPrefix}) deleted");
        return NoContent();
    }

    private async Task<IActionResult> SetEnabledAsync(int apiKeyKey, bool isEnabled)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var existing = await keys.GetAsync(apiKeyKey);
        if (existing is null || !await keys.SetEnabledAsync(apiKeyKey, isEnabled)) return NotFound();
        cache.Remove($"apikey-state:{apiKeyKey}");
        await auditLogger.LogAsync("ApiKeyChanged", user.UserKey, "api_key", apiKeyKey.ToString(),
            detail: $"API key '{existing.DisplayName}' ({existing.KeyPrefix}) {(isEnabled ? "enabled" : "disabled")}");
        return NoContent();
    }
}
