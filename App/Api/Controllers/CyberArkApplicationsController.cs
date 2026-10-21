using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Controllers;

/// <summary>D-282: Reports > CyberArk Applications (AAM application IDs from Self-Hosted Direct), under ViewDashboard like the other reports.</summary>
[ApiController]
[Route("api/reports/cyberark-applications")]
[Authorize(Policy = Permissions.ViewDashboard)]
public sealed class CyberArkApplicationsController(SafeAccessRepository repository) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repository.GetApplicationsAsync());
}
