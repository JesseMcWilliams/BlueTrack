using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/risk-exceptions")]
[Authorize]
public sealed class RiskExceptionsController(
    RiskExceptionRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    /// <summary>D-42: stacked filters (status/accountKey/scopeType) plus multi-column sort.</summary>
    [HttpGet]
    public async Task<IActionResult> GetList(
        [FromQuery] string? status = null,
        [FromQuery] long? accountKey = null,
        [FromQuery] string? scopeType = null,
        [FromQuery] string? sort = null)
    {
        var sortBy = SortParser.Parse(sort);
        return Ok(await repository.GetListAsync(status, accountKey, scopeType, sortBy));
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
        if (detail is null)
        {
            return NotFound();
        }

        var user = await currentUserResolver.ResolveAsync(User);
        if (user is not null)
        {
            await auditLogger.LogReadIfEnabledAsync(user.UserKey, "risk_exception", exceptionKey.ToString());
        }

        return Ok(detail);
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

        await auditLogger.LogAsync(
            "ExceptionApproved",
            user.UserKey,
            entityName: "risk_exception",
            entityKey: exceptionKey.ToString(),
            detail: $"Exception created, scoped to {(hasAccount ? $"AccountKey {request.AccountKey}" : $"ApplicationKey {request.ApplicationKey}")}",
            fieldChanges:
            [
                new FieldChange("Justification", null, request.Justification),
                new FieldChange("ReviewDate", null, request.ReviewDate.ToString("yyyy-MM-dd")),
                new FieldChange("ExternalTicketReference", null, request.ExternalTicketReference)
            ]);

        return CreatedAtAction(nameof(GetByKey), new { exceptionKey }, new { exceptionKey });
    }

    /// <summary>Re-approval (workflow step 4): extends ReviewDate without changing status.</summary>
    [HttpPut("{exceptionKey:int}/extend-review")]
    [Authorize(Policy = Permissions.ApproveExceptions)]
    public async Task<IActionResult> ExtendReview(int exceptionKey, [FromBody] ExtendReviewRequest request)
    {
        var before = await repository.GetByKeyAsync(exceptionKey);
        if (before is null)
        {
            return NotFound();
        }

        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        await repository.ExtendReviewAsync(exceptionKey, request.NewReviewDate);

        await auditLogger.LogAsync(
            "ExceptionReviewExtended",
            user.UserKey,
            entityName: "risk_exception",
            entityKey: exceptionKey.ToString(),
            fieldChanges: [new FieldChange("ReviewDate", before.ReviewDate.ToString("yyyy-MM-dd"), request.NewReviewDate.ToString("yyyy-MM-dd"))]);

        return NoContent();
    }

    /// <summary>Revocation (workflow step 4).</summary>
    [HttpPut("{exceptionKey:int}/revoke")]
    [Authorize(Policy = Permissions.ApproveExceptions)]
    public async Task<IActionResult> Revoke(int exceptionKey)
    {
        var before = await repository.GetByKeyAsync(exceptionKey);
        if (before is null)
        {
            return NotFound();
        }

        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null)
        {
            return Unauthorized();
        }

        await repository.RevokeAsync(exceptionKey);

        await auditLogger.LogAsync(
            "ExceptionRevoked",
            user.UserKey,
            entityName: "risk_exception",
            entityKey: exceptionKey.ToString(),
            fieldChanges: [new FieldChange("StatusName", before.StatusName, "Revoked")]);

        return NoContent();
    }
}
