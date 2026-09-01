using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/account-progress")]
[Authorize]
public sealed class AccountProgressController(AccountProgressRepository repository) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetList([FromQuery] string? stage = null)
    {
        var results = await repository.GetSummaryListAsync(stage);
        return Ok(results);
    }
}
