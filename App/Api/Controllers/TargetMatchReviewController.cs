using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

/// <summary>Design_Risk-Scoring.md, D-101-105, D-119 Phase B: the weak-match (IP-only, etc.) review queue, resolved as Linked/NewTarget/Ignored (D-229). Gated by ManageTargets per that doc's own suggestion.</summary>
[ApiController]
[Route("api/admin/risk-scoring/target-match-review")]
[Authorize(Policy = Permissions.ManageTargets)]
public sealed class TargetMatchReviewController(
    TargetMatchReviewRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetPending() => Ok(await repository.GetPendingAsync());

    [HttpPost("{targetMatchReviewKey:int}/resolve")]
    public async Task<IActionResult> Resolve(int targetMatchReviewKey, [FromBody] ResolveTargetMatchReviewRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var resolved = await repository.ResolveAsync(targetMatchReviewKey, request, user.UserKey);
        var detail = $"{resolved.IdentifierType}='{resolved.IdentifierValue}' resolved as {resolved.Resolution}";
        if (resolved.TargetKey is { } targetKey)
        {
            detail += resolved.PrimaryTargetKey is { } primaryKey
                ? $": target '{resolved.TargetName}' (key {targetKey}) linked to target '{resolved.PrimaryTargetName}' (key {primaryKey})"
                : $": new target '{resolved.TargetName}' (key {targetKey})";
        }
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "target_match_review", targetMatchReviewKey.ToString(), detail: detail);

        // D-229: the target Link or New Target made is audited on itself too, a link as a field change (as D-212's account links are).
        if (resolved.TargetKey is { } createdKey)
        {
            if (resolved.PrimaryTargetKey is { } linkedTo)
            {
                await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_target", createdKey.ToString(),
                    detail: $"Target '{resolved.TargetName}' created from Target Match Review and linked to target '{resolved.PrimaryTargetName}' (key {linkedTo})",
                    fieldChanges: [new FieldChange("LinkedToTargetKey", null, linkedTo.ToString())]);
            }
            else
            {
                await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_target", createdKey.ToString(),
                    detail: $"Target '{resolved.TargetName}' created from Target Match Review");
            }
        }
        return NoContent();
    }
}
