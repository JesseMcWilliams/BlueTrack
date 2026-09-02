using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/risk-exceptions")]
[Authorize]
public sealed class RiskExceptionsController(
    RiskExceptionRepository repository,
    CurrentUserResolver currentUserResolver) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetList([FromQuery] string? status = null)
    {
        return Ok(await repository.GetListAsync(status));
    }

    /// <summary>Approval worklist: every currently-Active exception (requires ApproveExceptions, D-07).</summary>
    [HttpGet("active")]
    [Authorize(Policy = Permissions.ApproveExceptions)]
    public async Task<IActionResult> GetActive()
    {
        return Ok(await repository.GetActiveAsync());
    }

    /// <summary>Overdue-review worklist: Active exceptions past ReviewDate (D-19).</summary>
    [HttpGet("overdue-review")]
    public async Task<IActionResult> GetOverdueReview()
    {
        return Ok(await repository.GetOverdueReviewAsync());
    }

    [HttpGet("{exceptionKey:int}")]
    public async Task<IActionResult> GetByKey(int exceptionKey)
    {
        var detail = await repository.GetByKeyAsync(exceptionKey);
        return detail is null ? NotFound() : Ok(detail);
    }

    [HttpPost]
    [Authorize(Policy = Permissions.ApproveExceptions)]
    public async Task<IActionResult> Create([FromBody] CreateRiskExceptionRequest request)
    {
        var hasAccount = request.AccountKey is not null;
        var hasApplication = request.ApplicationKey is not null;
        if (hasAccount == hasApplication)
        {
            // Exactly one must be set (D-18/Q-25) -- both or neither is invalid.
            return Problem(
                title: "Invalid exception scope",
                detail: "Exactly one of accountKey or applicationKey must be set.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        var exceptionKey = await repository.CreateAsync(request, user.UserKey);
        return CreatedAtAction(nameof(GetByKey), new { exceptionKey }, new { exceptionKey });
    }

    /// <summary>Re-approval (workflow step 4): extends ReviewDate without changing status.</summary>
    [HttpPut("{exceptionKey:int}/extend-review")]
    [Authorize(Policy = Permissions.ApproveExceptions)]
    public async Task<IActionResult> ExtendReview(int exceptionKey, [FromBody] ExtendReviewRequest request)
    {
        await repository.ExtendReviewAsync(exceptionKey, request.NewReviewDate);
        return NoContent();
    }

    /// <summary>Revocation (workflow step 4).</summary>
    [HttpPut("{exceptionKey:int}/revoke")]
    [Authorize(Policy = Permissions.ApproveExceptions)]
    public async Task<IActionResult> Revoke(int exceptionKey)
    {
        await repository.RevokeAsync(exceptionKey);
        return NoContent();
    }
}
