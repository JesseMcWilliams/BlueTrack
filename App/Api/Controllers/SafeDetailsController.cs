using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-308: one Safe's page, for anyone who may see tracked data. A controller of its own because SafesController
/// (the Application Mapping's Safe list) needs CurateApplicationMapping on every action.
/// </summary>
[ApiController]
[Route("api/safes/{safeKey:int}")]
[Authorize(Policy = Permissions.ViewDashboard)]
public sealed class SafeDetailsController(SafeDetailRepository repository) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(int safeKey) =>
        await repository.GetAsync(safeKey) is { } safe ? Ok(safe) : NotFound();
}
