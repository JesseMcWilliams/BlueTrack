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
    RiskExceptionRepository riskExceptionRepository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    /// <summary>
    /// D-42: multiple simultaneous filters (stage/status/riskLevel/owner)
    /// plus multi-column sort, e.g. sort=stageName:asc,ownerName:desc.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> GetList(
        [FromQuery] string? stage = null,
        [FromQuery] string? status = null,
        [FromQuery] string? riskLevel = null,
        [FromQuery] string? owner = null,
        [FromQuery] string? sort = null)
    {
        var sortBy = SortParser.Parse(sort);
        var results = await repository.GetSummaryListAsync(stage, status, riskLevel, owner, sortBy);
        return Ok(results);
    }

    /// <summary>
    /// The field-metadata-driven form's own definition list
    /// (Design_Interface_Extensibility.md) -- any authenticated user can
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
        return detail is null ? NotFound() : Ok(detail);
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

        var lockStatus = await lockRepository.GetStatusAsync(accountKey);
        if (lockStatus is null || lockStatus.LockedByUserKey != user.UserKey)
        {
            return Conflict("This record is not locked by you -- acquire the edit lock before saving.");
        }

        var before = await repository.GetDetailAsync(accountKey);
        if (before is null)
        {
            return NotFound();
        }

        // D-51 rule 1: Complete requires ActualCompletionDate.
        var newStatusName = await repository.GetStatusNameAsync(request.CurrentStatusKey);
        if (newStatusName == "Complete" && request.ActualCompletionDate is null)
        {
            return Problem(title: "Validation failed", detail: "ActualCompletionDate is required when Status is set to Complete.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // D-51 rule 2: a stage regression (lower StageOrder) requires a Reason.
        var isRegression = false;
        if (request.CurrentStageKey != before.CurrentStageKey)
        {
            var oldOrder = await repository.GetStageOrderAsync(before.CurrentStageKey);
            var newOrder = await repository.GetStageOrderAsync(request.CurrentStageKey);
            isRegression = oldOrder is not null && newOrder is not null && newOrder < oldOrder;
            if (isRegression && string.IsNullOrWhiteSpace(request.Reason))
            {
                return Problem(title: "Validation failed", detail: "A Reason is required when regressing to an earlier Blueprint stage.",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }

        // Risk Exception wiring (Design_Risk_Exception_Tracking.md workflow
        // steps 1-2): status can't be set to Risk Accepted / Excluded
        // without linking an Active exception scoped to this account.
        // Cleared for every other status -- ExceptionKey only means anything
        // while the account is actually in that status (per the column's
        // own documented contract in 06_BlueTrack_WebInterface_Schema.sql).
        // Application-scoped exceptions can't be linked from here yet -- the
        // design itself leaves the batch propagation to every account under
        // that application as "an implementation detail for later, not
        // decided here."
        int? resolvedExceptionKey = null;
        if (newStatusName == "Risk Accepted / Excluded")
        {
            if (request.ExceptionKey is null)
            {
                return Problem(title: "Validation failed",
                    detail: "An Active exception must be linked (or created) before setting status to Risk Accepted / Excluded.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var exception = await riskExceptionRepository.GetByKeyAsync(request.ExceptionKey.Value);
            if (exception is null || exception.AccountKey != accountKey || exception.StatusName != "Active")
            {
                return Problem(title: "Validation failed",
                    detail: "The linked exception must be an Active exception scoped to this account.",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            resolvedExceptionKey = exception.ExceptionKey;
        }

        await repository.UpdateAsync(accountKey, request, resolvedExceptionKey);
        await lockRepository.ReleaseAsync(accountKey, user.UserKey);

        List<FieldChange> changes = [];
        void AddIfChanged(string name, object? oldValue, object? newValue)
        {
            var oldText = oldValue?.ToString();
            var newText = newValue?.ToString();
            if (oldText != newText) changes.Add(new FieldChange(name, oldText, newText));
        }
        AddIfChanged("CurrentStageKey", before.CurrentStageKey, request.CurrentStageKey);
        AddIfChanged("CurrentStatusKey", before.CurrentStatusKey, request.CurrentStatusKey);
        AddIfChanged("RiskLevelKey", before.RiskLevelKey, request.RiskLevelKey);
        AddIfChanged("AccountTypeKey", before.AccountTypeKey, request.AccountTypeKey);
        AddIfChanged("SORKey", before.SORKey, request.SORKey);
        AddIfChanged("OwnerName", before.OwnerName, request.OwnerName);
        AddIfChanged("BusinessUnit", before.BusinessUnit, request.BusinessUnit);
        AddIfChanged("TargetRemediationDate", before.TargetRemediationDate?.ToString("yyyy-MM-dd"), request.TargetRemediationDate?.ToString("yyyy-MM-dd"));
        AddIfChanged("ActualCompletionDate", before.ActualCompletionDate?.ToString("yyyy-MM-dd"), request.ActualCompletionDate?.ToString("yyyy-MM-dd"));
        AddIfChanged("Notes", before.Notes, request.Notes);
        AddIfChanged("ExceptionKey", before.ExceptionKey, resolvedExceptionKey);

        if (changes.Count > 0)
        {
            await auditLogger.LogAsync("FieldEdit", user.UserKey, "fact_account_progress", accountKey.ToString(),
                reason: isRegression ? request.Reason : null, fieldChanges: changes);
        }

        return NoContent();
    }
}
