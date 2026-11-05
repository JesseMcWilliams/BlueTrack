using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-349: Admin > Risk Score, Defaults and Findings (ManageRiskScoreBands, as Bands). The default scores are also
/// readable by any signed-in user, for the Add Target and Add Access Group forms' starting values.
/// </summary>
[ApiController]
public sealed class RiskDefaultsController(
    RiskDefaultsRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    public const int MaxFindings = 2000;

    [HttpGet("api/risk-score/defaults")]
    [Authorize]
    public async Task<IActionResult> GetForForms()
    {
        var defaults = await repository.GetAsync();
        return Ok(new { defaults.DefaultTargetRiskScore, defaults.DefaultAccessGroupRiskScore });
    }

    [HttpGet("api/admin/risk-score/defaults")]
    [Authorize(Policy = Permissions.ManageRiskScoreBands)]
    public async Task<IActionResult> Get() => Ok(await repository.GetAsync());

    [HttpPut("api/admin/risk-score/defaults")]
    [Authorize(Policy = Permissions.ManageRiskScoreBands)]
    public async Task<IActionResult> Save([FromBody] SaveRiskDefaultsRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var errors = new List<string>();
        if (request.DefaultAddressTargetRiskScore is < 0 or > 1000 || request.DefaultTargetRiskScore is < 0 or > 1000
            || request.DefaultAccessGroupRiskScore is < 0 or > 1000)
            errors.Add("Default scores are 0 to 1,000.");
        if (request.FindingTypes.Any(f => f.RiskValue is < 0 or > 1000)) errors.Add("Finding risk values are 0 to 1,000.");
        var known = await repository.GetFindingTypeNamesAsync();
        var unknown = request.FindingTypes.Select(f => f.FindingType).Where(f => !known.Contains(f)).Distinct().ToList();
        if (unknown.Count > 0) errors.Add($"Not a kind of finding: {string.Join(", ", unknown)}.");
        if (request.FindingTypes.GroupBy(f => f.FindingType).Any(g => g.Count() > 1)) errors.Add("Each kind of finding once.");
        if (errors.Count > 0) return Problem(title: "Invalid risk defaults", detail: string.Join(" ", errors), statusCode: StatusCodes.Status400BadRequest);

        var before = await repository.GetAsync();
        var rescore = await repository.SaveAsync(request);
        var after = await repository.GetAsync();
        await auditLogger.LogAsync("RiskDefaultsChanged", user.UserKey, "app_config", entityKey: null,
            detail: AuditLogger.ClipDetail(Describe(before, after) + (rescore ? " Every risk score is marked to recalculate." : "")));
        return Ok(new { defaults = after, rescore });
    }

    [HttpGet("api/admin/risk-score/findings")]
    [Authorize(Policy = Permissions.ManageRiskScoreBands)]
    public async Task<IActionResult> Findings() => Ok(new
    {
        total = await repository.CountFindingsAsync(),
        max = MaxFindings,
        findings = await repository.GetFindingsAsync(MaxFindings)
    });

    /// <summary>Rebuilds the findings from the DNA data now. Scores change at the next recalculation.</summary>
    [HttpPost("api/admin/risk-score/findings/refresh")]
    [Authorize(Policy = Permissions.ManageRiskScoreBands)]
    public async Task<IActionResult> Refresh()
    {
        await repository.DeriveAsync();
        return Ok(new { total = await repository.CountFindingsAsync() });
    }

    private static string Describe(RiskDefaults before, RiskDefaults after)
    {
        var parts = new List<string>();
        void Score(string name, int b, int a) { if (b != a) parts.Add($"{name}: {b} -> {a}"); }
        Score("Target made from an account's address", before.DefaultAddressTargetRiskScore, after.DefaultAddressTargetRiskScore);
        Score("New target", before.DefaultTargetRiskScore, after.DefaultTargetRiskScore);
        Score("New access group", before.DefaultAccessGroupRiskScore, after.DefaultAccessGroupRiskScore);
        foreach (var a in after.FindingTypes)
        {
            var b = before.FindingTypes.FirstOrDefault(t => t.FindingType == a.FindingType);
            if (b is null) continue;
            if (b.RiskValue != a.RiskValue) parts.Add($"{a.DisplayName}: {b.RiskValue} -> {a.RiskValue}");
            if (b.IsEnabled != a.IsEnabled) parts.Add($"{a.DisplayName}: {(a.IsEnabled ? "now counts" : "no longer counts")}");
        }
        return parts.Count == 0 ? "Risk defaults saved; nothing changed." : "Risk defaults: " + string.Join("; ", parts) + ".";
    }
}
