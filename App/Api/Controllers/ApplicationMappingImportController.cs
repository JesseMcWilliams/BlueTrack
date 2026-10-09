using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Imports;
using BlueTrack.Api.RiskScoring;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-180: the HTTP side of the Application ↔ Safe Mapping page's two CSV
/// imports -- routes, permission, templates, reading the upload. The import
/// logic lives in ApplicationMappingImportService (Data Sources phase 1),
/// shared with the scheduled data feeds.
/// </summary>
[ApiController]
[Route("api/admin/application-mapping/import")]
[Authorize(Policy = Permissions.CurateApplicationMapping)]
public sealed class ApplicationMappingImportController(
    ApplicationMappingImportService importService,
    CurrentUserResolver currentUserResolver) : ControllerBase
{
    [HttpGet("applications/template")]
    public IActionResult GetApplicationsTemplate() =>
        File(Encoding.UTF8.GetBytes(string.Join(',', ApplicationMappingImportService.ApplicationColumns) + "\r\n"), "text/csv", "applications-template.csv");

    [HttpGet("safe-assignments/template")]
    public IActionResult GetSafeAssignmentsTemplate() =>
        File(Encoding.UTF8.GetBytes("SafeName,Application,Source\r\n"), "text/csv", "safe-assignments-template.csv");

    [HttpPost("applications")]
    public async Task<IActionResult> ImportApplications(IFormFile file)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var rows = await CsvFileReader.ReadRowsAsync(file.OpenReadStream());
        return Ok(await importService.ImportApplicationsAsync(rows, user.UserKey));
    }

    [HttpPost("safe-assignments")]
    public async Task<IActionResult> ImportSafeAssignments(IFormFile file)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var rows = await CsvFileReader.ReadRowsAsync(file.OpenReadStream());
        return Ok(await importService.ImportSafeAssignmentsAsync(rows, user.UserKey));
    }
}
