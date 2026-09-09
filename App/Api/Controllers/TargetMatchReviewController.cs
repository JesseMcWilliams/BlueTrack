using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

/// <summary>Design_Risk_Scoring.md, D-101-105, D-119 Phase B: the weak-match (IP-only, etc.) review queue. Gated by ManageTargets per that doc's own suggestion.</summary>
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

        var (identifierType, identifierValue) = await repository.ResolveAsync(targetMatchReviewKey, request, user.UserKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "target_match_review", targetMatchReviewKey.ToString(),
            detail: $"{identifierType}='{identifierValue}' resolved as {request.Resolution}");
        return NoContent();
    }
}
