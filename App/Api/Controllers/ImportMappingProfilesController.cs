using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

/// <summary>Design_Risk_Scoring.md, D-101-105, D-119 Phase B. Gated by ManageTargets -- covers mapping profiles for both Target- and Access-Group-related feeds; a narrower per-feed-type permission wasn't judged worth the extra granularity for a config surface this small.</summary>
[ApiController]
[Route("api/admin/risk-scoring/import-mapping-profiles")]
[Authorize(Policy = Permissions.ManageTargets)]
public sealed class ImportMappingProfilesController(
    ImportMappingProfileRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repository.GetAllAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveImportMappingProfileRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var key = await repository.CreateAsync(request, user.UserKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "import_mapping_profile", key.ToString(), detail: $"Mapping profile '{request.ProfileName}' ({request.FeedType}) created");
        return CreatedAtAction(nameof(GetAll), new { }, new { importMappingProfileKey = key });
    }

    [HttpPut("{profileKey:int}")]
    public async Task<IActionResult> Update(int profileKey, [FromBody] SaveImportMappingProfileRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.UpdateAsync(profileKey, request, user.UserKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "import_mapping_profile", profileKey.ToString(), detail: $"Mapping profile '{request.ProfileName}' updated");
        return NoContent();
    }

    [HttpDelete("{profileKey:int}")]
    public async Task<IActionResult> Delete(int profileKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.DeleteAsync(profileKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "import_mapping_profile", profileKey.ToString(), detail: "Mapping profile deleted");
        return NoContent();
    }
}
