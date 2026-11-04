using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-314: Admin > Power BI. It names the SQL Server and database, which is deployment information, so it needs
/// ViewDeploymentInfo like Admin > Deployment.
/// </summary>
[ApiController]
[Route("api/admin/power-bi")]
[Authorize(Policy = Permissions.ViewDeploymentInfo)]
public sealed class PowerBiController(PowerBiRepository repository) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get() => Ok(await repository.GetAsync());
}
