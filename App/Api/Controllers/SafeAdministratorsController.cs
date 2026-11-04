using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Reports;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-335: Reports > Safe Administrators (the least-privilege view), under ViewDashboard like Reports > Safe Access.
/// The page gets every row and filters them itself; the export takes the page's two choices: the view (by Safe or by
/// person) and whether CyberArk's built-in users and groups are included.
/// </summary>
[ApiController]
[Route("api/reports/safe-administrators")]
[Authorize(Policy = Permissions.ViewDashboard)]
public sealed class SafeAdministratorsController(SafeAdministratorsRepository repository, ReportExportService exportService) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get() => Ok(await repository.GetAsync());

    [HttpGet("export")]
    public async Task<IActionResult> Export([FromQuery] string? format, [FromQuery] string? view, [FromQuery] bool includeBuiltIn = false)
    {
        var byPerson = string.Equals(view, "person", StringComparison.OrdinalIgnoreCase);
        var rows = (await repository.GetAsync()).Where(r => includeBuiltIn || !r.IsBuiltIn).ToList();
        var parameters = new Dictionary<string, string?> { ["view"] = byPerson ? "person" : "safe", ["includeBuiltIn"] = includeBuiltIn ? "true" : null };
        return byPerson
            ? await exportService.ExportAsync(this, ReportExports.SafeAdministratorsByPerson, format, parameters,
                SafeAdministratorsRepository.ByPerson(rows), ReportExports.SafeAdministratorsByPersonColumns)
            : await exportService.ExportAsync(this, ReportExports.SafeAdministrators, format, parameters,
                rows, ReportExports.SafeAdministratorsColumns);
    }
}
