using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.AccountProgress;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-283: Admin > Progress Rules -- the rules the nightly load uses to move accounts to the next Blueprint stage, and
/// Test (how many accounts a rule would move now). ManageProgressRules; changes are audit-logged as ProgressRuleChanged.
/// </summary>
[ApiController]
[Route("api/admin/progress-rules")]
[Authorize(Policy = Permissions.ManageProgressRules)]
public sealed class ProgressRulesController(
    ProgressRuleRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    private const string AuditEventType = "ProgressRuleChanged";
    public const int MaxConditions = 20;

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repository.GetRulesAsync());

    /// <summary>The fields a condition can test and the operators each takes.</summary>
    [HttpGet("fields")]
    public IActionResult GetFields() => Ok(ProgressRuleFields.All);

    [HttpPost]
    public Task<IActionResult> Create([FromBody] SaveProgressRuleRequest request) => SaveAsync(null, request);

    [HttpPut("{ruleKey:int}")]
    public Task<IActionResult> Update(int ruleKey, [FromBody] SaveProgressRuleRequest request) => SaveAsync(ruleKey, request);

    [HttpDelete("{ruleKey:int}")]
    public async Task<IActionResult> Delete(int ruleKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        var name = await repository.DeleteRuleAsync(ruleKey);
        if (name is null) return NotFoundRule();
        await auditLogger.LogAsync(AuditEventType, user.UserKey, "progress_rule", ruleKey.ToString(), detail: AuditLogger.ClipDetail($"Progress Rule '{name}' deleted"));
        return NoContent();
    }

    /// <summary>Test, on unsaved values: the accounts at the stage the conditions would move now. Writes nothing.</summary>
    [HttpPost("preview")]
    public async Task<IActionResult> Preview([FromBody] SaveProgressRuleRequest request)
    {
        if (await ValidateAsync(request, needName: false) is { } bad) return bad;
        return Ok(await repository.PreviewAsync(request.FromStageKey!.Value, request.Conditions!));
    }

    private async Task<IActionResult> SaveAsync(int? ruleKey, SaveProgressRuleRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        if (await ValidateAsync(request, needName: true) is { } bad) return bad;

        var key = await repository.SaveRuleAsync(ruleKey, request, user.UserKey);
        if (key is null) return NotFoundRule();
        var conditions = string.Join(" AND ", request.Conditions!.Select(c => $"{c.FieldName} {c.Operator}{(ProgressRuleFields.NoValue.Contains(c.Operator!) ? "" : $" '{c.Value?.Trim()}'")}"));
        await auditLogger.LogAsync(AuditEventType, user.UserKey, "progress_rule", key.Value.ToString(),
            detail: AuditLogger.ClipDetail($"Progress Rule '{request.RuleName!.Trim()}' {(ruleKey is null ? "added" : "changed")}: from stage {request.FromStageKey} to {request.ToStageKey}, "
                + $"{(request.IsEnabled ? "enabled" : "disabled")}, when {conditions}"));
        return ruleKey is null ? CreatedAtAction(nameof(GetAll), new { }, new { progressRuleKey = key }) : NoContent();
    }

    private async Task<IActionResult?> ValidateAsync(SaveProgressRuleRequest request, bool needName)
    {
        if (needName && (string.IsNullOrWhiteSpace(request.RuleName) || request.RuleName.Trim().Length > 200))
            return Bad("Name the rule (at most 200 characters).");
        if (request.Description is { Length: > 1000 }) return Bad("The description can be at most 1,000 characters.");
        var fromOrder = request.FromStageKey is { } from ? await repository.GetStageOrderAsync(from) : null;
        if (fromOrder is null) return Bad("Choose the stage the rule moves accounts from.");
        var toOrder = request.ToStageKey is { } to ? await repository.GetStageOrderAsync(to) : null;
        if (toOrder is null) return Bad("Choose the stage the rule moves accounts to.");
        if (toOrder <= fromOrder) return Bad("A rule moves accounts forward: choose a later stage to move them to.");
        if (request.Conditions is not { Count: > 0 }) return Bad("Add at least one condition: a rule with none would move every account at the stage.");
        if (request.Conditions.Count > MaxConditions) return Bad($"A rule can have at most {MaxConditions} conditions.");
        foreach (var condition in request.Conditions)
        {
            if (ProgressRuleFields.Problem(condition) is { } problem) return Bad(problem);
        }
        return null;
    }

    private ObjectResult Bad(string detail) => Problem(title: "Can't save the rule", detail: detail, statusCode: StatusCodes.Status400BadRequest);

    private ObjectResult NotFoundRule() => Problem(title: "Rule not found", detail: "The rule no longer exists. Reload the page.", statusCode: StatusCodes.Status404NotFound);
}

/// <summary>
/// D-284: Admin > Blueprint Stages -- rename, reorder, add and delete the stages, mark where new accounts start and
/// which stage each KPI milestone counts from. ManageProgressRules; audit-logged as BlueprintStageChanged.
/// </summary>
[ApiController]
[Route("api/admin/blueprint-stages")]
[Authorize(Policy = Permissions.ManageProgressRules)]
public sealed class BlueprintStagesController(
    ProgressRuleRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    private const string AuditEventType = "BlueprintStageChanged";

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repository.GetStagesAsync());

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] SaveBlueprintStageRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        if (BadName(request.StageName) is { } bad) return bad;
        var name = request.StageName!.Trim();
        return await RunAsync(async () =>
        {
            var key = await repository.AddStageAsync(name);
            await auditLogger.LogAsync(AuditEventType, user.UserKey, "dim_blueprint_stage", key.ToString(), detail: AuditLogger.ClipDetail($"Stage '{name}' added at the end"));
            return CreatedAtAction(nameof(GetAll), new { }, new { stageKey = key });
        });
    }

    [HttpPut("{stageKey:int}")]
    public async Task<IActionResult> Update(int stageKey, [FromBody] SaveBlueprintStageRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        if (BadName(request.StageName) is { } bad) return bad;
        var milestone = string.IsNullOrWhiteSpace(request.Milestone) ? null : request.Milestone;
        if (milestone is not null && !ProgressRuleRepository.Milestones.Contains(milestone))
            return Problem(title: "Can't save the stage", detail: "The milestone is Onboarded, Managed or Compliant, or none.", statusCode: StatusCodes.Status400BadRequest);
        var name = request.StageName!.Trim();
        return await RunAsync(async () =>
        {
            var before = await repository.UpdateStageAsync(stageKey, name, request.IsStartStage, milestone);
            if (before is null) return NotFoundStage();
            await auditLogger.LogAsync(AuditEventType, user.UserKey, "dim_blueprint_stage", stageKey.ToString(), detail: AuditLogger.ClipDetail($"Stage '{name}' saved"),
                fieldChanges: [
                    new FieldChange("StageName", before.StageName, name),
                    new FieldChange("IsStartStage", before.IsStartStage.ToString(), request.IsStartStage.ToString()),
                    new FieldChange("Milestone", before.Milestone, milestone)
                ]);
            return NoContent();
        });
    }

    [HttpPut("order")]
    public async Task<IActionResult> Reorder([FromBody] ReorderBlueprintStagesRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        if (request.StageKeys is not { Count: > 0 }) return Problem(title: "Can't reorder", detail: "Give every stage, in the new order.", statusCode: StatusCodes.Status400BadRequest);
        return await RunAsync(async () =>
        {
            await repository.ReorderStagesAsync(request.StageKeys);
            await auditLogger.LogAsync(AuditEventType, user.UserKey, "dim_blueprint_stage", null, detail: AuditLogger.ClipDetail($"Stages reordered: {string.Join(", ", request.StageKeys)}"));
            return NoContent();
        });
    }

    [HttpDelete("{stageKey:int}")]
    public async Task<IActionResult> Delete(int stageKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        return await RunAsync(async () =>
        {
            var name = await repository.DeleteStageAsync(stageKey);
            if (name is null) return NotFoundStage();
            await auditLogger.LogAsync(AuditEventType, user.UserKey, "dim_blueprint_stage", stageKey.ToString(), detail: AuditLogger.ClipDetail($"Stage '{name}' deleted"));
            return NoContent();
        });
    }

    private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (ProgressRuleConflictException ex)
        {
            return Problem(title: "Can't change the stages", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private ObjectResult? BadName(string? name) => string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100
        ? Problem(title: "Can't save the stage", detail: "Name the stage (at most 100 characters).", statusCode: StatusCodes.Status400BadRequest)
        : null;

    private ObjectResult NotFoundStage() => Problem(title: "Stage not found", detail: "The stage no longer exists. Reload the page.", statusCode: StatusCodes.Status404NotFound);
}
