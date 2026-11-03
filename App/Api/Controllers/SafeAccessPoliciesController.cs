using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-300: Admin > Safe Access Policies -- the Safe roles, the policies the nightly load checks every Safe against, and
/// Check now. ManageSafeAccessPolicies; changes are audit-logged as SafeAccessPolicyChanged.
/// </summary>
[ApiController]
[Route("api/admin/safe-access")]
[Authorize(Policy = Permissions.ManageSafeAccessPolicies)]
public sealed class SafeAccessPoliciesController(
    SafeAccessPolicyRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    private const string AuditEventType = "SafeAccessPolicyChanged";
    public const int MaxRules = 20;

    /// <summary>The Safe permissions a role can name.</summary>
    [HttpGet("permissions")]
    public async Task<IActionResult> GetPermissions() => Ok(await repository.GetPermissionNamesAsync());

    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles() => Ok(await repository.GetRolesAsync());

    [HttpPost("roles")]
    public Task<IActionResult> CreateRole([FromBody] SaveSafeRoleRequest request) => SaveRoleAsync(null, request);

    [HttpPut("roles/{roleKey:int}")]
    public Task<IActionResult> UpdateRole(int roleKey, [FromBody] SaveSafeRoleRequest request) => SaveRoleAsync(roleKey, request);

    [HttpDelete("roles/{roleKey:int}")]
    public async Task<IActionResult> DeleteRole(int roleKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        return await RunAsync(async () =>
        {
            var name = await repository.DeleteRoleAsync(roleKey);
            if (name is null) return NotFoundProblem("Safe role");
            await auditLogger.LogAsync(AuditEventType, user.UserKey, "safe_role", roleKey.ToString(), detail: AuditLogger.ClipDetail($"Safe role '{name}' deleted"));
            return NoContent();
        });
    }

    [HttpGet("policies")]
    public async Task<IActionResult> GetPolicies() => Ok(await repository.GetPoliciesAsync());

    [HttpPost("policies")]
    public Task<IActionResult> CreatePolicy([FromBody] SaveSafeAccessPolicyRequest request) => SavePolicyAsync(null, request);

    [HttpPut("policies/{policyKey:int}")]
    public Task<IActionResult> UpdatePolicy(int policyKey, [FromBody] SaveSafeAccessPolicyRequest request) => SavePolicyAsync(policyKey, request);

    [HttpDelete("policies/{policyKey:int}")]
    public async Task<IActionResult> DeletePolicy(int policyKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        var name = await repository.DeletePolicyAsync(policyKey);
        if (name is null) return NotFoundProblem("Policy");
        await auditLogger.LogAsync(AuditEventType, user.UserKey, "safe_access_policy", policyKey.ToString(), detail: AuditLogger.ClipDetail($"Safe access policy '{name}' deleted"));
        return NoContent();
    }

    /// <summary>Check now: checks every Safe against the saved policies at once, as the nightly load does.</summary>
    [HttpPost("check")]
    public async Task<IActionResult> Check()
    {
        await repository.CheckAsync();
        return NoContent();
    }

    private async Task<IActionResult> SaveRoleAsync(int? roleKey, SaveSafeRoleRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.RoleName) || request.RoleName.Trim().Length > 100) return Bad("Name the role (at most 100 characters).");
        if (request.Description is { Length: > 1000 }) return Bad("The description can be at most 1,000 characters.");
        if (request.Permissions is not { Count: > 0 }) return Bad("Choose at least one Safe permission.");
        var known = (await repository.GetPermissionNamesAsync()).ToHashSet(StringComparer.Ordinal);
        if (request.Permissions.FirstOrDefault(p => !known.Contains(p)) is { } unknown) return Bad($"'{unknown}' isn't a Safe permission.");

        return await RunAsync(async () =>
        {
            var key = await repository.SaveRoleAsync(roleKey, request, user.UserKey);
            if (key is null) return NotFoundProblem("Safe role");
            await auditLogger.LogAsync(AuditEventType, user.UserKey, "safe_role", key.Value.ToString(),
                detail: AuditLogger.ClipDetail($"Safe role '{request.RoleName!.Trim()}' {(roleKey is null ? "added" : "changed")}: "
                    + $"{string.Join(", ", request.Permissions.Distinct())}{(request.ExactMatch ? " (and no others)" : "")}"));
            return roleKey is null ? CreatedAtAction(nameof(GetRoles), new { }, new { safeRoleKey = key }) : NoContent();
        });
    }

    private async Task<IActionResult> SavePolicyAsync(int? policyKey, SaveSafeAccessPolicyRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        if (string.IsNullOrWhiteSpace(request.PolicyName) || request.PolicyName.Trim().Length > 200) return Bad("Name the policy (at most 200 characters).");
        if (string.IsNullOrWhiteSpace(request.SafeNamePattern) || request.SafeNamePattern.Trim().Length > 300)
            return Bad("Give the Safe names the policy applies to (* is any text; at most 300 characters).");
        if (request.Description is { Length: > 1000 }) return Bad("The description can be at most 1,000 characters.");
        if (request.Rules is not { Count: > 0 }) return Bad("Add at least one rule.");
        if (request.Rules.Count > MaxRules) return Bad($"A policy can have at most {MaxRules} rules.");
        var roles = await repository.GetRoleKeysAsync();
        foreach (var rule in request.Rules)
        {
            if (rule.RuleType is not (SafeAccessPolicyRule.Required or SafeAccessPolicyRule.Forbidden)) return Bad("A rule is Required or Forbidden.");
            if (string.IsNullOrWhiteSpace(rule.MemberPattern) || rule.MemberPattern.Trim().Length > 300)
                return Bad("Give each rule the member names it matches ({SafeName} is the Safe's name, * any text; at most 300 characters).");
            if (rule.RuleType == SafeAccessPolicyRule.Required && rule.SafeRoleKey is null) return Bad("A Required rule needs the Safe role the member must hold.");
            if (rule.SafeRoleKey is { } roleKey && !roles.Contains(roleKey)) return Bad("A rule names a Safe role that no longer exists. Reload the page.");
        }

        return await RunAsync(async () =>
        {
            var key = await repository.SavePolicyAsync(policyKey, request, user.UserKey);
            if (key is null) return NotFoundProblem("Policy");
            var rules = string.Join("; ", request.Rules.Select(r => $"{r.RuleType} {r.MemberPattern!.Trim()}{(r.SafeRoleKey is { } k ? $" role {k}" : "")}{(r.RuleType == SafeAccessPolicyRule.Required && r.RequirePeople ? " with people" : "")}"));
            await auditLogger.LogAsync(AuditEventType, user.UserKey, "safe_access_policy", key.Value.ToString(),
                detail: AuditLogger.ClipDetail($"Safe access policy '{request.PolicyName!.Trim()}' {(policyKey is null ? "added" : "changed")}: Safes {request.SafeNamePattern!.Trim()}, "
                    + $"order {request.SortOrder}, {(request.IsEnabled ? "enabled" : "disabled")}; {rules}"));
            return policyKey is null ? CreatedAtAction(nameof(GetPolicies), new { }, new { safeAccessPolicyKey = key }) : NoContent();
        });
    }

    private async Task<IActionResult> RunAsync(Func<Task<IActionResult>> action)
    {
        try
        {
            return await action();
        }
        catch (SafeAccessConflictException ex)
        {
            return Problem(title: "Can't save", detail: ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private ObjectResult Bad(string detail) => Problem(title: "Can't save", detail: detail, statusCode: StatusCodes.Status400BadRequest);

    private ObjectResult NotFoundProblem(string what) =>
        Problem(title: $"{what} not found", detail: "It no longer exists. Reload the page.", statusCode: StatusCodes.Status404NotFound);
}

/// <summary>
/// D-300/D-301: Reports > Safe Access (ViewDashboard), an account's Safe check and review (ViewDashboard to read), and
/// marking or clearing reviews (ReviewAccountCompliance; audit-logged as AccountComplianceReviewed).
/// </summary>
[ApiController]
[Authorize]
public sealed class AccountComplianceController(
    SafeAccessPolicyRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    private const string AuditEventType = "AccountComplianceReviewed";

    [HttpGet("api/reports/safe-access")]
    [Authorize(Policy = Permissions.ViewDashboard)]
    public async Task<IActionResult> GetReport() => Ok(await repository.GetReportAsync());

    [HttpGet("api/account-progress/{accountKey:long}/compliance")]
    [Authorize(Policy = Permissions.ViewDashboard)]
    public async Task<IActionResult> Get(long accountKey) =>
        await repository.GetAccountComplianceAsync(accountKey) is { } compliance ? Ok(compliance) : NotFound();

    [HttpPut("api/account-progress/{accountKey:long}/compliance-review")]
    [Authorize(Policy = Permissions.ReviewAccountCompliance)]
    public async Task<IActionResult> Review(long accountKey, [FromBody] ComplianceReviewRequest? request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        if (request?.Note is { Length: > 1000 }) return Problem(title: "Can't save the review", detail: "The note can be at most 1,000 characters.", statusCode: StatusCodes.Status400BadRequest);
        if (!await repository.ReviewAccountAsync(accountKey, user.UserKey, request?.Note)) return NotFound();
        await auditLogger.LogAsync(AuditEventType, user.UserKey, "fact_account", accountKey.ToString(),
            detail: AuditLogger.ClipDetail($"Compliance reviewed{(string.IsNullOrWhiteSpace(request?.Note) ? "" : $": {request.Note.Trim()}")}"));
        return NoContent();
    }

    [HttpDelete("api/account-progress/{accountKey:long}/compliance-review")]
    [Authorize(Policy = Permissions.ReviewAccountCompliance)]
    public async Task<IActionResult> Clear(long accountKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        if (!await repository.ClearReviewAsync(accountKey)) return NotFound();
        await auditLogger.LogAsync(AuditEventType, user.UserKey, "fact_account", accountKey.ToString(), detail: "Compliance review cleared");
        return NoContent();
    }

    /// <summary>Marks every account in the Safe not reviewed yet as reviewed (one review each). Returns how many.</summary>
    [HttpPost("api/reports/safe-access/{safeKey:int}/review")]
    [Authorize(Policy = Permissions.ReviewAccountCompliance)]
    public async Task<IActionResult> ReviewSafe(int safeKey, [FromBody] ComplianceReviewRequest? request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        if (request?.Note is { Length: > 1000 }) return Problem(title: "Can't save the review", detail: "The note can be at most 1,000 characters.", statusCode: StatusCodes.Status400BadRequest);
        var reviewed = await repository.ReviewSafeAccountsAsync(safeKey, user.UserKey, request?.Note);
        if (reviewed is null) return NotFound();
        foreach (var accountKey in reviewed)
        {
            await auditLogger.LogAsync(AuditEventType, user.UserKey, "fact_account", accountKey.ToString(),
                detail: AuditLogger.ClipDetail($"Compliance reviewed with its Safe{(string.IsNullOrWhiteSpace(request?.Note) ? "" : $": {request.Note.Trim()}")}"));
        }
        return Ok(new { reviewedCount = reviewed.Count });
    }
}
