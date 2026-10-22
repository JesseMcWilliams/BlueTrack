using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Reports;

namespace BlueTrack.Api.Controllers;

/// <summary>D-288: Reports > CPM Health -- the accounts whose CPM status is failure -- and its export, under ViewDashboard like the other reports.</summary>
[ApiController]
[Route("api/reports/cpm-health")]
[Authorize(Policy = Permissions.ViewDashboard)]
public sealed class CpmHealthController(CpmHealthRepository repository, ReportExportService exportService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get() => Ok(await repository.GetAsync());

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] string? format) =>
        await exportService.ExportAsync(this, ReportExports.CpmHealth, format, ReportExportService.NoParameters,
            await repository.GetAsync(), ReportExports.CpmHealthColumns);
}
