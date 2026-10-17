using System.Net.Mail;
using BlueTrack.Api.ApiAccess;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-243: Admin > API Keys. Creates, lists, edits, rotates, disables and deletes the keys scripts and other
/// systems exchange for tokens (ApiTokenController). The key itself is shown once, in the response to the
/// create (or the rotate), and never again: only its hash is stored.
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
    public const int MaxRateLimit = 100000;

    /// <summary>OwnerEmail: who is told when the key is about to expire. RateLimitPerMinute: this key's own limit (blank = the global one).</summary>
    public sealed record CreateApiKeyRequest(string? DisplayName, int AppRoleKey, int? LifetimeDays, string? OwnerEmail = null, int? RateLimitPerMinute = null);

    /// <summary>ExpiresOn: the last day the key works (yyyy-MM-dd, up to a year from today); blank leaves the expiry as it is.</summary>
    public sealed record UpdateApiKeyRequest(string? DisplayName, int AppRoleKey, string? ExpiresOn, string? OwnerEmail, int? RateLimitPerMinute);

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await keys.ListAsync());

    [HttpGet("{apiKeyKey:int}")]
    public async Task<IActionResult> Get(int apiKeyKey) => await keys.GetAsync(apiKeyKey) is { } key ? Ok(key) : NotFound();

    /// <summary>The roles a key may hold (every role but Admin), the default lifetime and the longest allowed.</summary>
    [HttpGet("options")]
    public async Task<IActionResult> GetOptions() => Ok(new
    {
        Roles = (await roles.GetRolesAsync()).Where(r => r.RoleName != ApiAccessRules.AdminRoleName).Select(r => new { r.AppRoleKey, r.RoleName }),
        DefaultLifetimeDays = await keys.GetDefaultLifetimeDaysAsync(),
        MaxLifetimeDays = ApiAccessRules.MaxLifetimeDays,
        MaxRateLimitPerMinute = MaxRateLimit,
    });

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateApiKeyRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var name = request.DisplayName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) return BadRequest(new { error = "Name is required (100 characters at most)." });
        if (await CheckRoleAsync(request.AppRoleKey) is { } roleError) return roleError;
        if (!TryOwnerEmail(request.OwnerEmail, out var ownerEmail)) return BadRequest(new { error = "The owner email isn't a valid email address." });
        if (request.RateLimitPerMinute is < 1 or > MaxRateLimit) return BadRequest(new { error = $"The rate limit must be 1 to {MaxRateLimit} requests a minute, or blank for the default." });

        var days = request.LifetimeDays ?? await keys.GetDefaultLifetimeDaysAsync();
        if (days < 1 || days > ApiAccessRules.MaxLifetimeDays) return BadRequest(new { error = $"Lifetime must be 1 to {ApiAccessRules.MaxLifetimeDays} days." });
        if (await keys.NameExistsAsync(name)) return Conflict(new { error = $"There is already an API key named '{name}'." });

        var roleName = (await keys.GetRoleNameAsync(request.AppRoleKey))!;
        var (key, prefix, hash) = ApiKeyGenerator.Create();
        var expiresAt = timeProvider.GetUtcNow().UtcDateTime.AddDays(days);
        var apiKeyKey = await keys.CreateAsync(name, prefix, hash, request.AppRoleKey, expiresAt, user.UserKey, ownerEmail, request.RateLimitPerMinute);
        await auditLogger.LogAsync("ApiKeyCreated", user.UserKey, "api_key", apiKeyKey.ToString(),
            detail: $"API key '{name}' ({prefix}) created with role {roleName}, expires {expiresAt:yyyy-MM-dd}");
        return StatusCode(StatusCodes.Status201Created, new { apiKeyKey, apiKey = key, keyPrefix = prefix, expiresAt });
    }

    /// <summary>Changes a key in place: name, role, expiry (renew or shorten), owner email and rate limit. The key itself and its tokens are unchanged.</summary>
    [HttpPut("{apiKeyKey:int}")]
    public async Task<IActionResult> Update(int apiKeyKey, [FromBody] UpdateApiKeyRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var existing = await keys.GetAsync(apiKeyKey);
        if (existing is null) return NotFound();

        var name = request.DisplayName?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 100) return BadRequest(new { error = "Name is required (100 characters at most)." });
        if (await CheckRoleAsync(request.AppRoleKey) is { } roleError) return roleError;
        if (!TryOwnerEmail(request.OwnerEmail, out var ownerEmail)) return BadRequest(new { error = "The owner email isn't a valid email address." });
        if (request.RateLimitPerMinute is < 1 or > MaxRateLimit) return BadRequest(new { error = $"The rate limit must be 1 to {MaxRateLimit} requests a minute, or blank for the default." });
        if (name != existing.DisplayName && await keys.NameExistsAsync(name)) return Conflict(new { error = $"There is already an API key named '{name}'." });

        DateTime? expiresAt = null;
        if (!string.IsNullOrWhiteSpace(request.ExpiresOn))
        {
            if (!DateTime.TryParseExact(request.ExpiresOn.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day))
            {
                return BadRequest(new { error = "Expires on must be a date as yyyy-MM-dd." });
            }
            // The key works through the end of that day (UTC).
            expiresAt = DateTime.SpecifyKind(day.Date.AddDays(1).AddSeconds(-1), DateTimeKind.Utc);
            var now = timeProvider.GetUtcNow().UtcDateTime;
            if (expiresAt <= now) return BadRequest(new { error = "Expires on must be today or later. To stop a key now, disable or delete it." });
            if (expiresAt > now.AddDays(ApiAccessRules.MaxLifetimeDays + 1)) return BadRequest(new { error = $"A key can't last more than {ApiAccessRules.MaxLifetimeDays} days from today." });
            if (expiresAt.Value.Date == existing.ExpiresAt.Date) expiresAt = null;   // same day: leave the time as it is
        }

        var roleName = (await keys.GetRoleNameAsync(request.AppRoleKey))!;
        await keys.UpdateAsync(apiKeyKey, name, request.AppRoleKey, expiresAt, ownerEmail, request.RateLimitPerMinute);
        cache.Remove($"apikey-state:{apiKeyKey}");

        var changes = new List<FieldChange>();
        void Track(string field, string? before, string? after)
        {
            if ((before ?? "") != (after ?? "")) changes.Add(new FieldChange(field, before, after));
        }
        Track("DisplayName", existing.DisplayName, name);
        Track("Role", existing.RoleName, roleName);
        Track("ExpiresAt", existing.ExpiresAt.ToString("yyyy-MM-dd"), (expiresAt ?? existing.ExpiresAt).ToString("yyyy-MM-dd"));
        Track("OwnerEmail", existing.OwnerEmail, ownerEmail);
        Track("RateLimitPerMinute", existing.RateLimitPerMinute?.ToString(), request.RateLimitPerMinute?.ToString());
        if (changes.Count > 0)
        {
            await auditLogger.LogAsync("ApiKeyChanged", user.UserKey, "api_key", apiKeyKey.ToString(),
                detail: $"API key '{existing.DisplayName}' ({existing.KeyPrefix}) changed", fieldChanges: changes);
        }
        return NoContent();
    }

    /// <summary>
    /// A new key with the same role (and owner and rate limit) as this one, for switching a caller over: the old key
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
        var newKey = await keys.CreateAsync(name, prefix, hash, existing.AppRoleKey, expiresAt, user.UserKey, existing.OwnerEmail, existing.RateLimitPerMinute);
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

    /// <summary>A role a key may hold: it must exist and not be Admin. Null when fine.</summary>
    private async Task<IActionResult?> CheckRoleAsync(int appRoleKey)
    {
        var roleName = await keys.GetRoleNameAsync(appRoleKey);
        if (roleName is null) return BadRequest(new { error = "That role does not exist." });
        if (roleName == ApiAccessRules.AdminRoleName) return BadRequest(new { error = "An API key can't hold the Admin role. Choose a narrower role." });
        return null;
    }

    /// <summary>Blank is fine (no owner); otherwise one plain email address.</summary>
    private static bool TryOwnerEmail(string? value, out string? normalized)
    {
        normalized = null;
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return true;
        if (trimmed.Length > 320 || !MailAddress.TryCreate(trimmed, out var address) || address.Address != trimmed) return false;
        normalized = trimmed;
        return true;
    }
}
