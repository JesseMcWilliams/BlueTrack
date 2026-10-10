using BlueTrack.Api.Audit;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.AccountProgress;

/// <summary>
/// The rules for saving one Account Progress record, shared by the edit
/// page (AccountProgressController.Update) and bulk edit (D-182) so both
/// apply them identically: D-51's Complete-needs-a-date and
/// regression-needs-a-reason rules, the Risk Exception link rules
/// (Design_Risk-Exception-Tracking.md) with optional segregation of duties,
/// then the update and its FieldEdit audit entry. Edit locks are the
/// callers' concern.
/// </summary>
public sealed class AccountProgressSaveService(
    AccountProgressRepository repository,
    RiskExceptionRepository riskExceptionRepository,
    AppConfigRepository appConfigRepository,
    AuditLogger auditLogger)
{
    public sealed record SaveResult(bool Saved, bool Changed, string? Error)
    {
        public static SaveResult Invalid(string error) => new(false, false, error);
    }

    /// <param name="keepUnchangedException">
    /// Bulk edit: when the status stays Risk Accepted / Excluded and the
    /// link isn't being changed, keep the existing link without re-checking
    /// it, so editing, say, the owner of such an account isn't refused over
    /// a link the bulk edit didn't touch. The edit page re-checks it.
    /// </param>
    public async Task<SaveResult> SaveAsync(long accountKey, AccountProgressDetail before, SaveAccountProgressRequest request,
        int userKey, bool keepUnchangedException = false, string? auditDetail = null)
    {
        // D-51 rule 1: Complete requires ActualCompletionDate.
        var newStatusName = await repository.GetStatusNameAsync(request.CurrentStatusKey);
        if (newStatusName == "Complete" && request.ActualCompletionDate is null)
        {
            return SaveResult.Invalid("ActualCompletionDate is required when Status is set to Complete.");
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
                return SaveResult.Invalid("A Reason is required when regressing to an earlier Blueprint stage.");
            }
        }

        // Risk Exception wiring (Design_Risk-Exception-Tracking.md workflow
        // steps 1-2): status can't be set to Risk Accepted / Excluded
        // without linking an Active exception scoped to this account.
        // Cleared for every other status -- ExceptionKey only means anything
        // while the account is actually in that status (per the column's
        // own documented contract in 04_BlueTrack_Baseline_WebSchema.sql).
        // Application-scoped exceptions can't be linked from here yet -- the
        // design itself leaves the batch propagation to every account under
        // that application as "an implementation detail for later, not
        // decided here."
        int? resolvedExceptionKey = null;
        if (newStatusName == "Risk Accepted / Excluded")
        {
            if (keepUnchangedException && request.CurrentStatusKey == before.CurrentStatusKey
                && request.ExceptionKey == before.ExceptionKey && before.ExceptionKey is not null)
            {
                resolvedExceptionKey = before.ExceptionKey;
            }
            else
            {
                if (request.ExceptionKey is null)
                {
                    return SaveResult.Invalid("An Active exception must be linked (or created) before setting status to Risk Accepted / Excluded.");
                }

                var exception = await riskExceptionRepository.GetByKeyAsync(request.ExceptionKey.Value);
                if (exception is null || exception.AccountKey != accountKey || exception.StatusName != "Active")
                {
                    return SaveResult.Invalid("The linked exception must be an Active exception scoped to this account.");
                }

                // Segregation of duties (admin-configurable, off by default --
                // some organizations don't have separate staff for the two
                // roles): the person who approved this exception can't also be
                // the one linking it here.
                var appConfig = await appConfigRepository.GetAsync();
                if (appConfig.EnforceRiskExceptionSegregationOfDuties && exception.ApprovedBy == userKey)
                {
                    return SaveResult.Invalid("This exception was approved by you -- segregation of duties requires a different person to link it to an account.");
                }

                resolvedExceptionKey = exception.ExceptionKey;
            }
        }

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

        await repository.UpdateAsync(accountKey, request, resolvedExceptionKey);

        if (changes.Count > 0)
        {
            await auditLogger.LogAsync("FieldEdit", userKey, "fact_account_progress", accountKey.ToString(),
                detail: auditDetail, reason: isRegression ? request.Reason : null, fieldChanges: changes);
        }

        return new SaveResult(true, changes.Count > 0, null);
    }
}
