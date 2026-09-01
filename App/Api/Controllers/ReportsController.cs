using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    [HttpGet("reconciliation-review-queue")]
    public async Task<IActionResult> GetReconciliationReviewQueue()
    {
        var results = await repository.GetReconciliationReviewQueueAsync();
        return Ok(results);
    }
}
