using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/account-progress")]
[Authorize]
public sealed class AccountProgressController(
    AccountProgressRepository repository,
    FieldMetadataRepository fieldMetadataRepository,
    ReferenceDataRepository referenceDataRepository,
    AccountProgressLockRepository lockRepository,
    AppConfigRepository appConfigRepository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger,
    AccountProgress.AccountProgressSaveService saveService) : ControllerBase
{
    /// <summary>
    /// D-42: multiple simultaneous filters (stage/status/riskLevel/owner)
    /// plus multi-column sort, e.g. sort=stageName:asc,ownerName:desc.
    /// </summary>
    /// <summary>D-124 Phase 3: page/pageSize add server-side paging; X-Filtered-Count carries how many rows match the current filter, ignoring paging.</summary>
    [HttpGet]
    public async Task<IActionResult> GetList(
        [FromQuery] string? stage = null,
        [FromQuery] string? status = null,
        [FromQuery] string? riskLevel = null,
        [FromQuery] string? owner = null,
        [FromQuery] string? sort = null,
        [FromQuery] int? page = null,
        [FromQuery] int? pageSize = null,
        [FromQuery] string? search = null,
        [FromQuery] string? deleted = null)
    {
        // D-178: search matches Username or Address ("contains").
        // D-185: deleted = Hide (default) / Show / Only.
        var sortBy = SortParser.Parse(sort);
        var results = await repository.GetSummaryListAsync(stage, status, riskLevel, owner, sortBy, page, pageSize, search, deleted);
        Response.Headers["X-Total-Count"] = (await repository.GetTotalCountAsync()).ToString();
        Response.Headers["X-Filtered-Count"] = (await repository.GetFilteredCountAsync(stage, status, riskLevel, owner, search, deleted)).ToString();
        return Ok(results);
    }

    /// <summary>
    /// D-182: "Select all matching" for bulk edit -- the keys of every
    /// account matching the same filters as the list. Returns no keys when
    /// more accounts match than one bulk edit may change, so the page can
    /// say so instead of selecting a partial set. Any signed-in user (it
    /// reveals no more than the list does): it serves bulk edit and, since
    /// D-185, bulk delete/undelete, which need different permissions.
    /// </summary>
    [HttpGet("keys")]
    public async Task<IActionResult> GetMatchingKeys(
        [FromQuery] string? stage = null,
        [FromQuery] string? status = null,
        [FromQuery] string? riskLevel = null,
        [FromQuery] string? owner = null,
        [FromQuery] string? search = null,
        [FromQuery] string? deleted = null)
    {
        var max = (await appConfigRepository.GetAsync()).BulkEditMaxAccounts;
        var matching = await repository.GetFilteredCountAsync(stage, status, riskLevel, owner, search, deleted);
        var keys = matching > max ? [] : await repository.GetFilteredKeysAsync(stage, status, riskLevel, owner, search, max, deleted);
        return Ok(new AccountProgressKeysResult { MatchingCount = matching, MaxAccounts = max, AccountKeys = keys });
    }

    /// <summary>D-182: one set of field values applied to many accounts; see AccountProgressBulkEditService.</summary>
    [HttpPost("bulk-edit")]
    [Authorize(Policy = Permissions.EditAccountProgress)]
    public async Task<IActionResult> BulkEdit([FromBody] BulkEditAccountProgressRequest request,
        [FromServices] AccountProgress.AccountProgressBulkEditService bulkEditService)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        if (await bulkEditService.ValidateAsync(request) is { } error)
        {
            return Problem(title: "Invalid bulk edit", detail: error, statusCode: StatusCodes.Status400BadRequest);
        }
        return Ok(await bulkEditService.ApplyAsync(request, user.UserKey));
    }

    /// <summary>D-185: delete accounts in BlueTrack (one or many), with a required reason.</summary>
    [HttpPost("delete")]
    [Authorize(Policy = Permissions.DeleteAccounts)]
    public Task<IActionResult> Delete([FromBody] AccountDeletionRequest request,
        [FromServices] AccountProgress.AccountDeletionService deletionService) =>
        ApplyDeletionAsync(request, deletionService, delete: true);

    /// <summary>D-185: undo BlueTrack deletes (one or many), with a required reason.</summary>
    [HttpPost("undelete")]
    [Authorize(Policy = Permissions.DeleteAccounts)]
    public Task<IActionResult> Undelete([FromBody] AccountDeletionRequest request,
        [FromServices] AccountProgress.AccountDeletionService deletionService) =>
        ApplyDeletionAsync(request, deletionService, delete: false);

    private async Task<IActionResult> ApplyDeletionAsync(AccountDeletionRequest request, AccountProgress.AccountDeletionService deletionService, bool delete)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        if (await deletionService.ValidateAsync(request) is { } error)
        {
            return Problem(title: delete ? "Invalid delete" : "Invalid undelete", detail: error, statusCode: StatusCodes.Status400BadRequest);
        }
        return Ok(delete ? await deletionService.DeleteAsync(request, user.UserKey) : await deletionService.UndeleteAsync(request, user.UserKey));
    }

    /// <summary>D-185: every delete and undelete of this account in BlueTrack, newest first, with reasons.</summary>
    [HttpGet("{accountKey:long}/deletion-history")]
    public async Task<IActionResult> GetDeletionHistory(long accountKey, [FromServices] AccountDeletionRepository deletionRepository) =>
        Ok(await deletionRepository.GetHistoryAsync(accountKey));

    /// <summary>
    /// The field-metadata-driven form's own definition list
    /// (Design_Interface-Extensibility.md) -- any authenticated user can
    /// read this to render the edit form; managing the list itself is
    /// gated separately (ManageFieldMetadata, FieldMetadataController).
    /// </summary>
    [HttpGet("field-metadata")]
    public async Task<IActionResult> GetFieldMetadata()
    {
        return Ok(await fieldMetadataRepository.GetAllAsync());
    }

    [HttpGet("reference-data")]
    public async Task<IActionResult> GetReferenceData()
    {
        return Ok(await referenceDataRepository.GetAllReferenceDataAsync());
    }

    [HttpGet("{accountKey:long}")]
    public async Task<IActionResult> GetDetail(long accountKey)
    {
        var detail = await repository.GetDetailAsync(accountKey);
        if (detail is null)
        {
            return NotFound();
        }

        var user = await currentUserResolver.ResolveAsync(User);
        if (user is not null)
        {
            await auditLogger.LogReadIfEnabledAsync(user.UserKey, "fact_account_progress", accountKey.ToString());
        }

        return Ok(detail);
    }

    /// <summary>
    /// D-81: Active application-scoped exceptions covering this account
    /// (computed live) -- separate from ExceptionKey on the detail above,
    /// which only ever holds the account-scoped pointer (D-77). A account
    /// can be covered by both, neither, or an application one with no
    /// account-scoped one at all.
    /// </summary>
    [HttpGet("{accountKey:long}/application-exceptions")]
    public async Task<IActionResult> GetApplicationScopedExceptions(long accountKey)
    {
        return Ok(await repository.GetApplicationScopedExceptionsAsync(accountKey));
    }

    [HttpGet("{accountKey:long}/lock")]
    public async Task<IActionResult> GetLockStatus(long accountKey)
    {
        return Ok(await lockRepository.GetStatusAsync(accountKey));
    }

    /// <summary>Opening the edit form acquires the lock (D-50 mechanics step 1).</summary>
    [HttpPost("{accountKey:long}/lock")]
    [Authorize(Policy = Permissions.EditAccountProgress)]
    public async Task<IActionResult> AcquireLock(long accountKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var status = await lockRepository.TryAcquireAsync(accountKey, user.UserKey);
        if (status is null || status.LockedByUserKey != user.UserKey)
        {
            return Conflict(status);
        }
        return Ok(status);
    }

    /// <summary>Refreshes LastHeartbeatAt while the edit form stays open (D-50 mechanics step 3).</summary>
    [HttpPut("{accountKey:long}/lock/heartbeat")]
    [Authorize(Policy = Permissions.EditAccountProgress)]
    public async Task<IActionResult> Heartbeat(long accountKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var refreshed = await lockRepository.HeartbeatAsync(accountKey, user.UserKey);
        return refreshed ? NoContent() : Conflict();
    }

    /// <summary>Canceling releases the lock immediately (D-50 mechanics step 5).</summary>
    [HttpDelete("{accountKey:long}/lock")]
    [Authorize(Policy = Permissions.EditAccountProgress)]
    public async Task<IActionResult> ReleaseLock(long accountKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await lockRepository.ReleaseAsync(accountKey, user.UserKey);
        return NoContent();
    }

    /// <summary>
    /// Admin force-break of a stuck lock (D-50 mechanics step 6). Gated by
    /// EditAccountProgress, the same permission as editing itself -- the
    /// permission catalog (confirmed/fixed per D-05/D-61) has no separate
    /// "manage locks" permission, and adding one wasn't judged worth a
    /// design sign-off for this single action. Revisit if that's wrong.
    /// </summary>
    [HttpPost("{accountKey:long}/lock/force-release")]
    [Authorize(Policy = Permissions.EditAccountProgress)]
    public async Task<IActionResult> ForceReleaseLock(long accountKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await lockRepository.ForceReleaseAsync(accountKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "account_progress_lock", accountKey.ToString(), detail: "Lock force-released by admin");
        return NoContent();
    }

    [HttpPut("{accountKey:long}")]
    [Authorize(Policy = Permissions.EditAccountProgress)]
    public async Task<IActionResult> Update(long accountKey, [FromBody] SaveAccountProgressRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        // D-174: ownership, not liveness -- GetStatusAsync now hides a lock
        // whose heartbeat lapsed, which would otherwise refuse a save from
        // the holder who merely paused while nobody else took over.
        if (!await lockRepository.IsHeldByAsync(accountKey, user.UserKey))
        {
            return Conflict("This record is not locked by you -- acquire the edit lock before saving.");
        }

        var before = await repository.GetDetailAsync(accountKey);
        if (before is null)
        {
            return NotFound();
        }

        // D-51 and the Risk Exception link rules live in
        // AccountProgressSaveService, shared with bulk edit (D-182).
        var result = await saveService.SaveAsync(accountKey, before, request, user.UserKey);
        if (!result.Saved)
        {
            return Problem(title: "Validation failed", detail: result.Error, statusCode: StatusCodes.Status400BadRequest);
        }
        await lockRepository.ReleaseAsync(accountKey, user.UserKey);

        return NoContent();
    }

    /// <summary>
    /// D-101-105 Phase E: an analyst override on top of the computed risk
    /// score. A Reason is required whenever setting an override value
    /// (mirroring web.risk_exception.Justification's own "override needs a
    /// reason" precedent) but not when clearing one back to null -- reverting
    /// to the computed score needs no justification of its own.
    /// </summary>
    [HttpPut("{accountKey:long}/risk-score-override")]
    [Authorize(Policy = Permissions.EditAccountProgress)]
    public async Task<IActionResult> SetRiskScoreOverride(long accountKey, [FromBody] SaveRiskScoreOverrideRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        if (request.OverrideRiskScore is not null && string.IsNullOrWhiteSpace(request.Reason))
        {
            return Problem(title: "Validation failed", detail: "A Reason is required when setting a risk score override.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.OverrideRiskScore is < 0 or > 1000)
        {
            return Problem(title: "Validation failed", detail: "OverrideRiskScore must be between 0 and 1000.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var before = await repository.GetRiskScoreOverrideAsync(accountKey);
        await repository.SetRiskScoreOverrideAsync(accountKey, request.OverrideRiskScore, request.Reason, user.UserKey);

        await auditLogger.LogAsync("FieldEdit", user.UserKey, "account_risk_score", accountKey.ToString(),
            reason: request.Reason,
            fieldChanges: [new FieldChange("OverrideRiskScore", before.OverrideRiskScore?.ToString(), request.OverrideRiskScore?.ToString())]);

        return NoContent();
    }

    /// <summary>
    /// D-131: recalculates just this one account's ComputedRiskScore right
    /// now, regardless of its IsRiskScoreStale flag -- the bulk
    /// usp_RecalculateRiskScores (ReportsController's own "Recalculate Now")
    /// only ever touches rows already marked stale.
    /// </summary>
    [HttpPost("{accountKey:long}/recalculate-risk-score")]
    [Authorize(Policy = Permissions.EditAccountProgress)]
    public async Task<IActionResult> RecalculateRiskScore(long accountKey)
    {
        await repository.RecalculateForAccountAsync(accountKey);
        return NoContent();
    }
}
