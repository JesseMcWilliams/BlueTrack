using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/reports")]
[Authorize]
public sealed class ReportsController(ReportsRepository repository, RiskScoreReportRepository riskScoreReportRepository) : ControllerBase
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

    /// <summary>D-101-105 Phase E: the new Risk Score report, gated by ViewRiskReport per the plan approved for D-119. D-124 Phase 3: page/pageSize add server-side paging; X-Filtered-Count is always equal to X-Total-Count here (no filter params on this report) but is still sent for consistency with the other five paginated endpoints.</summary>
    [HttpGet("risk-score")]
    [Authorize(Policy = Permissions.ViewRiskReport)]
    public async Task<IActionResult> GetRiskScoreReport([FromQuery] string? sort = null, [FromQuery] int? page = null, [FromQuery] int? pageSize = null)
    {
        var sortBy = SortParser.Parse(sort);
        var results = await riskScoreReportRepository.GetSummaryListAsync(sortBy, page, pageSize);
        Response.Headers["X-Total-Count"] = (await riskScoreReportRepository.GetTotalCountAsync()).ToString();
        Response.Headers["X-Filtered-Count"] = (await riskScoreReportRepository.GetFilteredCountAsync()).ToString();
        return Ok(results);
    }

    [HttpGet("risk-score/{accountKey:long}/contributors")]
    [Authorize(Policy = Permissions.ViewRiskReport)]
    public async Task<IActionResult> GetRiskScoreContributors(long accountKey)
    {
        var results = await riskScoreReportRepository.GetContributorsAsync(accountKey);
        return Ok(results);
    }

    /// <summary>Manual trigger for usp_RecalculateRiskScores -- it also runs automatically inside usp_RunFullLoad, this is just an immediate refresh.</summary>
    [HttpPost("risk-score/recalculate")]
    [Authorize(Policy = Permissions.ViewRiskReport)]
    public async Task<IActionResult> RecalculateRiskScores()
    {
        await riskScoreReportRepository.RecalculateAllAsync();
        return NoContent();
    }
}
