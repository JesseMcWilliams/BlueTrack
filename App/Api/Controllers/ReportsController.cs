using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Reports;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/reports")]
[Authorize]
public sealed class ReportsController(
    ReportsRepository repository,
    RiskScoreReportRepository riskScoreReportRepository,
    RiskExceptionRepository riskExceptionRepository,
    DiscoveredAccountRepository discoveredAccountRepository,
    CurrentUserResolver currentUserResolver,
    DecommissionRepository decommissionRepository,
    ReportExportService exportService) : ControllerBase
{
    // D-220: each report's GET .../export?format=csv|xlsx sits beside it, under the
    // same policy, and takes the same filter/sort parameters the page sends.
    private static Dictionary<string, string?> Params(params (string Key, string? Value)[] values) =>
        values.ToDictionary(v => v.Key, v => v.Value);

    /// <summary>D-186: safes whose name matches the safe decommission pattern (not ignored safes). Same access as the other general reports.</summary>
    [Authorize(Policy = Permissions.ViewDashboard)]   // D-197: reading tracked data needs ViewDashboard, not just a sign-in
    [HttpGet("decom-safes")]
    public async Task<IActionResult> GetDecomSafes() => Ok(await decommissionRepository.GetSafesAsync());

    [Authorize(Policy = Permissions.ViewDashboard)]
    [HttpGet("decom-safes/export")]
    public async Task<IActionResult> ExportDecomSafes([FromQuery] string? format) =>
        await exportService.ExportAsync(this, ReportExports.DecomSafes, format, ReportExportService.NoParameters,
            await decommissionRepository.GetSafesAsync(), ReportExports.DecomSafesColumns);

    /// <summary>D-186: accounts in those safes, or whose name matches the account pattern, with any other safe holding the same account.</summary>
    [Authorize(Policy = Permissions.ViewDashboard)]   // D-197: reading tracked data needs ViewDashboard, not just a sign-in
    [HttpGet("decom-accounts")]
    public async Task<IActionResult> GetDecomAccounts() => Ok(await decommissionRepository.GetAccountsAsync());

    /// <summary>D-220: notDeletedOnly is the page's "Only accounts not yet deleted" box.</summary>
    [Authorize(Policy = Permissions.ViewDashboard)]
    [HttpGet("decom-accounts/export")]
    public async Task<IActionResult> ExportDecomAccounts([FromQuery] string? format, [FromQuery] bool notDeletedOnly = false)
    {
        var rows = await decommissionRepository.GetAccountsAsync();
        var shown = notDeletedOnly ? rows.Where(r => !r.IsDeleted).ToList() : rows;
        return await exportService.ExportAsync(this, ReportExports.DecomAccounts, format,
            Params(("notDeletedOnly", notDeletedOnly ? "true" : null)), shown, ReportExports.DecomAccountsColumns);
    }

    [Authorize(Policy = Permissions.ViewDashboard)]   // D-197: reading tracked data needs ViewDashboard, not just a sign-in
    [HttpGet("overdue-at-risk")]
    public async Task<IActionResult> GetOverdueAtRisk()
    {
        var results = await repository.GetOverdueAtRiskListAsync();
        return Ok(results);
    }

    [Authorize(Policy = Permissions.ViewDashboard)]
    [HttpGet("overdue-at-risk/export")]
    public async Task<IActionResult> ExportOverdueAtRisk([FromQuery] string? format) =>
        await exportService.ExportAsync(this, ReportExports.OverdueAtRisk, format, ReportExportService.NoParameters,
            await repository.GetOverdueAtRiskListAsync(), ReportExports.OverdueAtRiskColumns);

    [Authorize(Policy = Permissions.ViewDashboard)]   // D-197: reading tracked data needs ViewDashboard, not just a sign-in
    [HttpGet("stage-status-summary")]
    public async Task<IActionResult> GetStageStatusSummary()
    {
        var results = await repository.GetStageStatusFunnelSummaryAsync();
        return Ok(results);
    }

    /// <summary>D-220: exported as the page shows it -- a row per stage, a column per status, then Total.</summary>
    [Authorize(Policy = Permissions.ViewDashboard)]
    [HttpGet("stage-status-summary/export")]
    public async Task<IActionResult> ExportStageStatusSummary([FromQuery] string? format)
    {
        var table = ReportExports.StageStatusTable(await repository.GetStageStatusFunnelSummaryAsync());
        return await exportService.ExportAsync(this, ReportExports.StageStatusSummary, format, ReportExportService.NoParameters,
            table.Rows.Count, _ => Task.FromResult(table));
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

    [HttpGet("reconciliation-review-queue/export")]
    [Authorize(Policy = Permissions.ConfirmReconciliation)]
    public async Task<IActionResult> ExportReconciliationReviewQueue([FromQuery] string? format) =>
        await exportService.ExportAsync(this, ReportExports.ReconciliationReview, format, ReportExportService.NoParameters,
            await repository.GetReconciliationReviewQueueAsync(), ReportExports.ReconciliationReviewColumns);

    /// <summary>
    /// D-107/D-108: Safe entitlements granted to a member this app can't
    /// resolve to a known user/group. No specific permission required --
    /// read-only, informational, matching Overdue/At-Risk and Stage/Status
    /// Summary above.
    /// </summary>
    [Authorize(Policy = Permissions.ViewDashboard)]   // D-197: reading tracked data needs ViewDashboard, not just a sign-in
    [HttpGet("unresolved-entitlement-members")]
    public async Task<IActionResult> GetUnresolvedEntitlementMembers()
    {
        var results = await repository.GetUnresolvedEntitlementMembersAsync();
        return Ok(results);
    }

    [Authorize(Policy = Permissions.ViewDashboard)]
    [HttpGet("unresolved-entitlement-members/export")]
    public async Task<IActionResult> ExportUnresolvedEntitlementMembers([FromQuery] string? format) =>
        await exportService.ExportAsync(this, ReportExports.UnresolvedEntitlementMembers, format, ReportExportService.NoParameters,
            await repository.GetUnresolvedEntitlementMembersAsync(), ReportExports.UnresolvedEntitlementMembersColumns);

    /// <summary>D-143: Total/In Scope/Onboarded/Managed/Compliant account counts. No specific permission required, matching Overdue/At-Risk and Stage/Status Summary above -- read-only, aggregate counts only, no account-level detail.</summary>
    [Authorize(Policy = Permissions.ViewDashboard)]   // D-197: reading tracked data needs ViewDashboard, not just a sign-in
    [HttpGet("kpi-summary")]
    public async Task<IActionResult> GetKpiSummary()
    {
        var result = await repository.GetKpiSummaryAsync();
        return Ok(result);
    }

    [Authorize(Policy = Permissions.ViewDashboard)]
    [HttpGet("kpi-summary/export")]
    public async Task<IActionResult> ExportKpiSummary([FromQuery] string? format)
    {
        var table = ReportExports.KpiTable(await repository.GetKpiSummaryAsync());
        return await exportService.ExportAsync(this, ReportExports.KpiSummary, format, ReportExportService.NoParameters,
            table.Rows.Count, _ => Task.FromResult(table));
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

    /// <summary>D-220: every row (all pages) in the page's sort order.</summary>
    [HttpGet("risk-score/export")]
    [Authorize(Policy = Permissions.ViewRiskReport)]
    public async Task<IActionResult> ExportRiskScoreReport([FromQuery] string? format, [FromQuery] string? sort = null) =>
        await exportService.ExportAsync(this, ReportExports.RiskScore, format, Params(("sort", sort)),
            await riskScoreReportRepository.GetFilteredCountAsync(),
            async limit => ReportTable.From(ReportExports.RiskScoreColumns,
                await riskScoreReportRepository.GetExportRowsAsync(SortParser.Parse(sort), limit)));

    [HttpGet("risk-score/{accountKey:long}/contributors")]
    [Authorize(Policy = Permissions.ViewRiskReport)]
    public async Task<IActionResult> GetRiskScoreContributors(long accountKey)
    {
        var results = await riskScoreReportRepository.GetContributorsAsync(accountKey);
        return Ok(results);
    }

    /// <summary>Manual trigger for usp_RecalculateRiskScores -- it also runs automatically inside usp_RunFullLoad, this is just an immediate refresh.</summary>
    [HttpPost("risk-score/recalculate")]
    [Authorize(Policy = Permissions.ManageTargets)]   // D-198: it changes stored scores; a view permission isn't enough
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

    [HttpGet("risk-exception-sod/export")]
    [Authorize(Policy = Permissions.ViewRiskExceptionSodReport)]
    public async Task<IActionResult> ExportRiskExceptionSodReport([FromQuery] string? format) =>
        await exportService.ExportAsync(this, ReportExports.RiskExceptionSod, format, ReportExportService.NoParameters,
            await riskExceptionRepository.GetSegregationOfDutiesViolationsAsync(), ReportExports.RiskExceptionSodColumns);

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

    /// <summary>D-220: every New row (all pages) in the page's sort order.</summary>
    [HttpGet("discovered-accounts/export")]
    [Authorize(Policy = Permissions.ViewDiscoveredAccounts)]
    public async Task<IActionResult> ExportDiscoveredAccounts([FromQuery] string? format, [FromQuery] string? sort = null) =>
        await exportService.ExportAsync(this, ReportExports.DiscoveredAccounts, format, Params(("sort", sort)),
            await discoveredAccountRepository.GetFilteredCountAsync(),
            async limit => ReportTable.From(ReportExports.DiscoveredAccountsColumns,
                await discoveredAccountRepository.GetExportRowsAsync(SortParser.Parse(sort), limit)));

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
