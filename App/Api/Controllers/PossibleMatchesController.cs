using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-212 (Data Sources phase 4c): the Possible Account Matches report -- feed
/// accounts that may be a CyberArk account but didn't match it exactly -- and
/// its Link and Dismiss actions, all under ConfirmReconciliation.
/// </summary>
[ApiController]
[Route("api/reports/possible-matches")]
[Authorize(Policy = Permissions.ConfirmReconciliation)]
public sealed class PossibleMatchesController(
    PossibleMatchRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetOpen([FromQuery] int? page = null, [FromQuery] int? pageSize = null)
    {
        var count = (await repository.GetOpenCountAsync()).ToString();
        Response.Headers["X-Total-Count"] = count;
        Response.Headers["X-Filtered-Count"] = count;
        return Ok(await repository.GetOpenAsync(page, pageSize));
    }

    /// <summary>Links the pair, as an exact match would: the feed account is hidden and, at Discovered, moves to Onboarded to Vault.</summary>
    [HttpPost("{reconciliationKey:long}/link")]
    public async Task<IActionResult> Link(long reconciliationKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var match = await repository.ConfirmAsync(reconciliationKey, ReviewedBy(user));
        if (match is null) return NotOpen();

        List<FieldChange>? changes = match.NewStageKey is { } newStage
            ? [new FieldChange("CurrentStageKey", match.OldStageKey?.ToString(), newStage.ToString())]
            : null;
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "fact_account_progress", match.FeedAccountKey.ToString(),
            detail: AuditLogger.ClipDetail($"Linked to CyberArk account '{match.CyberArkAccountName}' (key {match.CyberArkAccountKey}) from the Possible Account Matches report"
                + (changes is null ? "" : "; moved to Onboarded to Vault")),
            fieldChanges: changes);
        return NoContent();
    }

    /// <summary>Not the same account: the pair is rejected and never proposed or linked again.</summary>
    [HttpPost("{reconciliationKey:long}/dismiss")]
    public async Task<IActionResult> Dismiss(long reconciliationKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        if (!await repository.DismissAsync(reconciliationKey, ReviewedBy(user))) return NotOpen();
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "account_reconciliation", reconciliationKey.ToString(),
            detail: "Possible account match dismissed (not the same account)");
        return NoContent();
    }

    // account_reconciliation.ReviewedBy is NVARCHAR(200).
    private static string ReviewedBy(Models.AppUser user)
    {
        var name = user.DisplayName ?? user.ExternalIdentifier;
        return name.Length > 200 ? name[..200] : name;
    }

    private ObjectResult NotOpen() => Problem(
        title: "This possible match is no longer open",
        detail: "It was already linked or dismissed, or one of the accounts was deleted. Reload the report.",
        statusCode: StatusCodes.Status409Conflict);
}
