using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.AdDiscovery;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-233: Admin > Account Classification -- the settings and rules that give each discovered account a class
/// (Primary / Secondary / Admin / Unknown) and an owner. Gated by ManageDiscoveredAccounts: the rules only change
/// how Discovered Accounts rows are labelled, the same data the per-row override changes, and need no directory
/// or credential access. Every change is audited (AccountClassification) and followed by a reclassify, which
/// needs no LDAP.
/// </summary>
[ApiController]
[Route("api/admin/account-classification")]
[Authorize(Policy = Permissions.ManageDiscoveredAccounts)]
public sealed class AccountClassificationController(
    AccountClassificationRepository repository,
    AccountClassificationService classificationService,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    private const string AuditEvent = "AccountClassification";

    /// <summary>The settings, every rule in evaluation order, and the choices the page offers.</summary>
    [HttpGet]
    public async Task<IActionResult> Get() => Ok(new
    {
        settings = await repository.GetSettingsAsync(),
        rules = await repository.GetRulesAsync(),
        allowedAttributes = AccountClassificationAttributes.Allowed,
        nameAttributes = AccountClassificationAttributes.NameAttributes,
        employeeIdAttributes = AccountClassificationAttributes.EmployeeIdAttributes,
        ruleClasses = AccountClasses.RuleClasses
    });

    [HttpPut("settings")]
    public async Task<IActionResult> SaveSettings([FromBody] SaveAccountClassificationSettingsRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var defaultClass = new[] { AccountClasses.Unknown, AccountClasses.Primary }
            .FirstOrDefault(c => string.Equals(c, request.DefaultAccountClass?.Trim() ?? AccountClasses.Unknown, StringComparison.OrdinalIgnoreCase));
        if (defaultClass is null) return Invalid("The class when no rule matches must be Unknown or Primary.");
        var employeeIdAttribute = AccountClassificationAttributes.EmployeeIdAttributes
            .FirstOrDefault(a => string.Equals(a, request.EmployeeIdAttribute?.Trim() ?? "employeeID", StringComparison.OrdinalIgnoreCase));
        if (employeeIdAttribute is null) return Invalid("The employee ID attribute must be employeeID or employeeNumber.");

        var old = await repository.GetSettingsAsync();
        var settings = new AccountClassificationSettings
        {
            IsEnabled = request.IsEnabled,
            DefaultAccountClass = defaultClass,
            OwnerByEmployeeId = request.OwnerByEmployeeId,
            EmployeeIdAttribute = employeeIdAttribute
        };
        await repository.SaveSettingsAsync(settings, user.UserKey);

        var changes = new List<FieldChange>();
        void Compare(string field, object oldValue, object newValue)
        {
            if (!Equals(oldValue, newValue)) changes.Add(new FieldChange(field, oldValue.ToString(), newValue.ToString()));
        }
        Compare("IsEnabled", old.IsEnabled, settings.IsEnabled);
        Compare("DefaultAccountClass", old.DefaultAccountClass, settings.DefaultAccountClass);
        Compare("OwnerByEmployeeId", old.OwnerByEmployeeId, settings.OwnerByEmployeeId);
        Compare("EmployeeIdAttribute", old.EmployeeIdAttribute, settings.EmployeeIdAttribute);
        await auditLogger.LogAsync(AuditEvent, user.UserKey, "account_classification_config", "1",
            detail: "Account classification settings saved", fieldChanges: changes);

        var reclassified = await classificationService.ReclassifyAsync();
        return Ok(new { reclassified });
    }

    [HttpPost("rules")]
    public async Task<IActionResult> CreateRule([FromBody] SaveAccountClassificationRuleRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var (error, rule) = AccountClassifier.Validate(request);
        if (error is not null) return Invalid(error);

        var key = await repository.CreateRuleAsync(rule!, user.UserKey);
        await auditLogger.LogAsync(AuditEvent, user.UserKey, "account_classification_rule", key.ToString(),
            detail: AuditLogger.ClipDetail($"Rule added ({(rule!.IsEnabled ? "enabled" : "disabled")}): {AccountClassifier.Describe(rule)}"));
        var reclassified = await classificationService.ReclassifyAsync();
        return Created("/api/admin/account-classification", new { accountClassificationRuleKey = key, reclassified });
    }

    [HttpPut("rules/{ruleKey:int}")]
    public async Task<IActionResult> UpdateRule(int ruleKey, [FromBody] SaveAccountClassificationRuleRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var (error, rule) = AccountClassifier.Validate(request);
        if (error is not null) return Invalid(error);

        var old = await repository.GetRuleAsync(ruleKey);
        if (old is null || !await repository.UpdateRuleAsync(ruleKey, rule!, user.UserKey)) return NotFound();

        await auditLogger.LogAsync(AuditEvent, user.UserKey, "account_classification_rule", ruleKey.ToString(),
            detail: AuditLogger.ClipDetail($"Rule updated: {AccountClassifier.Describe(rule!)}"),
            fieldChanges:
            [
                new FieldChange("Rule", AccountClassifier.Describe(old), AccountClassifier.Describe(rule!)),
                new FieldChange("IsEnabled", old.IsEnabled.ToString(), rule!.IsEnabled.ToString())
            ]);
        var reclassified = await classificationService.ReclassifyAsync();
        return Ok(new { reclassified });
    }

    [HttpDelete("rules/{ruleKey:int}")]
    public async Task<IActionResult> DeleteRule(int ruleKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var old = await repository.GetRuleAsync(ruleKey);
        if (old is null || !await repository.DeleteRuleAsync(ruleKey)) return NotFound();

        await auditLogger.LogAsync(AuditEvent, user.UserKey, "account_classification_rule", ruleKey.ToString(),
            detail: AuditLogger.ClipDetail($"Rule deleted: {AccountClassifier.Describe(old)}"));
        await classificationService.ReclassifyAsync();
        return NoContent();
    }

    /// <summary>Moves a rule one place up (earlier) or down within its type.</summary>
    [HttpPost("rules/{ruleKey:int}/move")]
    public async Task<IActionResult> MoveRule(int ruleKey, [FromBody] MoveAccountClassificationRuleRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var direction = request.Direction?.Trim().ToLowerInvariant();
        if (direction is not ("up" or "down")) return Invalid("Direction must be up or down.");

        var rule = await repository.GetRuleAsync(ruleKey);
        if (rule is null) return NotFound();
        if (!await repository.MoveRuleAsync(ruleKey, direction == "up", user.UserKey))
        {
            return Problem(title: "Can't move the rule", detail: $"The rule is already {(direction == "up" ? "first" : "last")} of its type.",
                statusCode: StatusCodes.Status409Conflict);
        }

        await auditLogger.LogAsync(AuditEvent, user.UserKey, "account_classification_rule", ruleKey.ToString(),
            detail: AuditLogger.ClipDetail($"Rule moved {direction}: {AccountClassifier.Describe(rule)}"));
        await classificationService.ReclassifyAsync();
        return NoContent();
    }

    /// <summary>Recomputes every discovered account's class and owner from what is stored (no LDAP).</summary>
    [HttpPost("reclassify")]
    public async Task<IActionResult> Reclassify()
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var reclassified = await classificationService.ReclassifyAsync();
        await auditLogger.LogAsync(AuditEvent, user.UserKey, "account_classification_config", "1",
            detail: $"Reclassify now: {reclassified} discovered accounts");
        return Ok(new { reclassified });
    }

    private ObjectResult Invalid(string detail) =>
        Problem(title: "Invalid account classification setting", detail: detail, statusCode: StatusCodes.Status400BadRequest);
}
