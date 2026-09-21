using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/reports")]
[Authorize]
public sealed class ReportsController(
    ReportsRepository repository,
    RiskScoreReportRepository riskScoreReportRepository,
    RiskExceptionRepository riskExceptionRepository,
    DiscoveredAccountRepository discoveredAccountRepository,
    CurrentUserResolver currentUserResolver) : ControllerBase
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

    /// <summary>
    /// Segregation-of-duties detective report (Option C): every historical
    /// case of the same user both approving a Risk Exception and linking it
    /// to an account, regardless of whether enforcement is (or ever was) on.
    /// </summary>
    [HttpGet("risk-exception-sod")]
    [Authorize(Policy = Permissions.ViewRiskExceptionSodReport)]
    public async Task<IActionResult> GetRiskExceptionSodReport()
    {
        var results = await riskExceptionRepository.GetSegregationOfDutiesViolationsAsync();
        return Ok(results);
    }

    /// <summary>AD Account Discovery feature (2026-09-16): real AD accounts not yet onboarded into CyberArk, matched against the Access Group inventory and risk-scored, gated by ViewDiscoveredAccounts. Defaults to Status='New' only -- Accept/Dismiss (below) move a row out of this default view without deleting it.</summary>
    [HttpGet("discovered-accounts")]
    [Authorize(Policy = Permissions.ViewDiscoveredAccounts)]
    public async Task<IActionResult> GetDiscoveredAccounts([FromQuery] string? sort = null, [FromQuery] int? page = null, [FromQuery] int? pageSize = null)
    {
        var sortBy = SortParser.Parse(sort);
        var results = await discoveredAccountRepository.GetListAsync(sortBy, page, pageSize);
        Response.Headers["X-Total-Count"] = (await discoveredAccountRepository.GetTotalCountAsync()).ToString();
        Response.Headers["X-Filtered-Count"] = (await discoveredAccountRepository.GetFilteredCountAsync()).ToString();
        return Ok(results);
    }

    /// <summary>Which Access Groups a discovered account matched -- mirrors the Risk Score report's own per-account contributor drill-down.</summary>
    [HttpGet("discovered-accounts/{discoveredAccountKey:int}/access-groups")]
    [Authorize(Policy = Permissions.ViewDiscoveredAccounts)]
    public async Task<IActionResult> GetDiscoveredAccountAccessGroups(int discoveredAccountKey)
    {
        var results = await discoveredAccountRepository.GetMatchedAccessGroupsAsync(discoveredAccountKey);
        return Ok(results.Select(r => new { r.AccessGroupKey, r.GroupName }));
    }

    /// <summary>
    /// Requested directly (2026-09-16): moves a candidate into onboarding
    /// tracking -- a real dbo.fact_account/fact_account_progress row,
    /// immediately visible in the Account Progress list. Gated separately
    /// from ViewDiscoveredAccounts (ManageDiscoveredAccounts) since this
    /// writes real account inventory data, not just views a report --
    /// mirrors the ViewDeploymentInfo/TriggerBackup split.
    /// </summary>
    [HttpPost("discovered-accounts/{discoveredAccountKey:int}/accept")]
    [Authorize(Policy = Permissions.ManageDiscoveredAccounts)]
    public async Task<IActionResult> AcceptDiscoveredAccount(int discoveredAccountKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var accountKey = await discoveredAccountRepository.AcceptAsync(discoveredAccountKey, user.UserKey);
        return Ok(new { accountKey });
    }

    /// <summary>False positive, or already onboarded (PossibleExistingAccountKey already flagged that) -- resolved with no dbo.fact_account write.</summary>
    [HttpPost("discovered-accounts/{discoveredAccountKey:int}/dismiss")]
    [Authorize(Policy = Permissions.ManageDiscoveredAccounts)]
    public async Task<IActionResult> DismissDiscoveredAccount(int discoveredAccountKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await discoveredAccountRepository.DismissAsync(discoveredAccountKey, user.UserKey);
        return NoContent();
    }
}
