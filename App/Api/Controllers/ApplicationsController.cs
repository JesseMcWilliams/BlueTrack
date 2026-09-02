using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Controllers;

/// <summary>Backs the application-scoping dropdown on the Risk Exception create/edit form.</summary>
[ApiController]
[Route("api/applications")]
[Authorize]
public sealed class ApplicationsController(ApplicationRepository repository) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetList()
    {
        return Ok(await repository.GetAllAsync());
    }
}
