using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-337: Admin > Data Sources, Import and Load: the latest runs of the nightly job's steps, the Load's procedures,
/// and what a run still going is doing in SQL Server. Read-only; under ManageDataSources like the rest of that page.
/// </summary>
[ApiController]
[Route("api/admin/etl-runs")]
[Authorize(Policy = Permissions.ManageDataSources)]
public sealed class EtlRunsController(EtlRunRepository repository, TimeProvider timeProvider) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get() => Ok(new
    {
        serverTime = timeProvider.GetUtcNow().UtcDateTime,
        runs = await repository.GetRecentAsync(12),
        running = await repository.GetRunningAsync()
    });

    [HttpGet("{etlRunKey:long}/steps")]
    public async Task<IActionResult> Steps(long etlRunKey) => Ok(await repository.GetStepsAsync(etlRunKey));
}
