using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/reports")]
[Authorize]
public sealed class ReportsController(ReportsRepository repository) : ControllerBase
{
    [HttpGet("overdue-at-risk")]
    public async Task<IActionResult> GetOverdueAtRisk()
    {
        var results = await repository.GetOverdueAtRiskListAsync();
        return Ok(results);
    }

    [HttpGet("stage-status-summary")]
    public async Task<IActionResult> GetStageStatusSummary()
    {
        var results = await repository.GetStageStatusFunnelSummaryAsync();
        return Ok(results);
    }

    /// <summary>
    /// Gated by ConfirmReconciliation per D-56 -- found ungated while
    /// building frontend permission-aware UI (which needs the backend gate
    /// to actually exist before it means anything to hide the link).
    /// </summary>
    [HttpGet("reconciliation-review-queue")]
    [Authorize(Policy = Permissions.ConfirmReconciliation)]
    public async Task<IActionResult> GetReconciliationReviewQueue()
    {
        var results = await repository.GetReconciliationReviewQueueAsync();
        return Ok(results);
    }

    /// <summary>
    /// D-107/D-108: Safe entitlements granted to a member this app can't
    /// resolve to a known user/group. No specific permission required --
    /// read-only, informational, matching Overdue/At-Risk and Stage/Status
    /// Summary above.
    /// </summary>
    [HttpGet("unresolved-entitlement-members")]
    public async Task<IActionResult> GetUnresolvedEntitlementMembers()
    {
        var results = await repository.GetUnresolvedEntitlementMembersAsync();
        return Ok(results);
    }
}
