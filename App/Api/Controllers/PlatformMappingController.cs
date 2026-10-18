using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-247: Admin > Platform Mapping. Gives each CyberArk platform (Privilege Cloud's own list, and Self-Hosted's, one
/// per PolicyID since D-244) the account type and source of record its accounts get by default. A mapping is a
/// suggestion: both stay editable on each account. Nothing here deletes or hides a platform.
/// </summary>
[ApiController]
[Route("api/admin/platform-mapping")]
[Authorize(Policy = Permissions.CuratePlatformMapping)]
public sealed class PlatformMappingController(
    PlatformMappingRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    public sealed record SaveMappingRequest(int AccountTypeKey, int? SorKey, string? Notes, bool FillExisting = false);

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repository.ListAsync());

    [HttpGet("options")]
    public async Task<IActionResult> GetOptions() => Ok(await repository.GetOptionsAsync());

    [HttpPut("{platformKey:int}")]
    public async Task<IActionResult> Save(int platformKey, [FromBody] SaveMappingRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var before = await repository.GetAsync(platformKey);
        if (before is null) return NotFound();
        var options = await repository.GetOptionsAsync();
        var type = options.AccountTypes.FirstOrDefault(t => t.Key == request.AccountTypeKey);
        if (type is null) return BadRequest(new { error = "Choose an account type." });
        PlatformMappingChoice? sor = null;
        if (request.SorKey is { } sorKey)
        {
            sor = options.SourcesOfRecord.FirstOrDefault(s => s.Key == sorKey);
            if (sor is null) return BadRequest(new { error = "That source of record doesn't exist." });
        }
        var notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim();
        if (notes is { Length: > 500 }) return BadRequest(new { error = "Notes are 500 characters at most." });

        var result = await repository.SaveAsync(platformKey, type.Key, sor?.Key, notes, request.FillExisting);

        var changes = new List<FieldChange>();
        void Track(string field, string? old, string? now)
        {
            if ((old ?? "") != (now ?? "")) changes.Add(new FieldChange(field, old, now));
        }
        Track("AccountType", before.AccountTypeName, type.Name);
        Track("SourceOfRecord", before.SourceOfRecord, sor?.Name);
        Track("Notes", before.Notes, notes);
        var filled = request.FillExisting ? $"; filled {result.AccountsTypeFilled} account(s) with no account type and {result.AccountsSorFilled} with no source of record" : "";
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "platform_account_type_map", platformKey.ToString(),
            detail: $"Platform '{before.PlatformId}' ({before.SourceSystemName}) mapped to {type.Name}{(sor is null ? "" : $" / {sor.Name}")}{filled}",
            fieldChanges: changes.Count > 0 ? changes : null);
        return Ok(result);
    }

    [HttpDelete("{platformKey:int}")]
    public async Task<IActionResult> Remove(int platformKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var before = await repository.GetAsync(platformKey);
        if (before is null || !await repository.RemoveAsync(platformKey)) return NotFound();
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "platform_account_type_map", platformKey.ToString(),
            detail: $"Mapping of platform '{before.PlatformId}' ({before.SourceSystemName}) removed; accounts keep what they have",
            fieldChanges: [new FieldChange("AccountType", before.AccountTypeName, null)]);
        return NoContent();
    }
}
