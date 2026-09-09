using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.RiskScoring;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-120: single add/edit/delete for the named risk score bands that
/// translate a computed EffectiveRiskScore into a label -- mirrors
/// AccessGroupsController's shape (no nested child collection).
/// </summary>
[ApiController]
[Route("api/admin/risk-score-bands")]
[Authorize(Policy = Permissions.ManageRiskScoreBands)]
public sealed class RiskScoreBandsController(
    RiskScoreBandRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repository.GetAllAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveRiskScoreBandRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        try
        {
            var key = await repository.CreateAsync(request, user.UserKey);
            await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_risk_score_band", key.ToString(), detail: $"Risk Score Band '{request.BandName}' created");
            return CreatedAtAction(nameof(GetAll), new { }, new { riskScoreBandKey = key });
        }
        catch (RiskScoreBandOverlapException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{riskScoreBandKey:int}")]
    public async Task<IActionResult> Update(int riskScoreBandKey, [FromBody] SaveRiskScoreBandRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        try
        {
            await repository.UpdateAsync(riskScoreBandKey, request, user.UserKey);
            await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_risk_score_band", riskScoreBandKey.ToString(), detail: $"Risk Score Band '{request.BandName}' updated");
            return NoContent();
        }
        catch (RiskScoreBandOverlapException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpDelete("{riskScoreBandKey:int}")]
    public async Task<IActionResult> Delete(int riskScoreBandKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.DeleteAsync(riskScoreBandKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_risk_score_band", riskScoreBandKey.ToString(), detail: "Risk Score Band deleted");
        return NoContent();
    }
}
