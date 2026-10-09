using BlueTrack.Api.Audit;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.AccountProgress;

/// <summary>
/// D-182: applies one bulk edit to each selected account in turn, through
/// the same AccountProgressSaveService the edit page uses, so every rule
/// and every per-account FieldEdit audit entry is the same as a single
/// edit. Agreed 2026-10-09:
///   - An account someone else is editing (a live edit lock) is skipped and
///     reported, not overridden. Each account is locked while it's saved.
///   - One account failing a rule doesn't stop the others; it's reported.
///   - At most app_config.BulkEditMaxAccounts accounts (default 500).
///   - One summary 'BulkEdit' audit event per bulk edit.
/// Risk Exception isn't a bulk field: a link must be an exception scoped to
/// that one account, so one value can't be valid for several accounts.
/// </summary>
public sealed class AccountProgressBulkEditService(
    AccountProgressRepository repository,
    AccountProgressLockRepository lockRepository,
    AccountProgressSaveService saveService,
    AppConfigRepository appConfigRepository,
    AuditLogger auditLogger)
{
    public static readonly IReadOnlySet<string> EditableFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CurrentStageKey", "CurrentStatusKey", "RiskLevelKey", "AccountTypeKey", "SORKey",
        "OwnerName", "BusinessUnit", "TargetRemediationDate", "ActualCompletionDate", "Notes"
    };

    /// <summary>Null when the request is usable; otherwise why not (a 400 for the whole request).</summary>
    public async Task<string?> ValidateAsync(BulkEditAccountProgressRequest request)
    {
        var keys = request.AccountKeys?.Distinct().Count() ?? 0;
        if (keys == 0) return "Select at least one account.";
        var max = (await appConfigRepository.GetAsync()).BulkEditMaxAccounts;
        if (keys > max) return $"A bulk edit can change at most {max} accounts; {keys} were selected.";
        if (request.Fields is not { Count: > 0 }) return "Choose at least one field to change.";
        var unknown = request.Fields.Where(f => !EditableFields.Contains(f)).ToList();
        if (unknown.Count > 0) return $"These fields can't be bulk edited: {string.Join(", ", unknown)}.";
        if (Has(request, "CurrentStageKey") && (request.CurrentStageKey is null || await repository.GetStageOrderAsync(request.CurrentStageKey.Value) is null))
            return "Choose a valid Stage.";
        if (Has(request, "CurrentStatusKey") && (request.CurrentStatusKey is null || await repository.GetStatusNameAsync(request.CurrentStatusKey.Value) is null))
            return "Choose a valid Status.";
        if (request.NotesMode is not (null or "Replace" or "Append")) return "NotesMode must be Replace or Append.";
        if (Has(request, "Notes") && request.NotesMode == "Append" && string.IsNullOrWhiteSpace(request.Notes)) return "Enter the text to add to Notes.";
        return null;
    }

    private static bool Has(BulkEditAccountProgressRequest request, string field) =>
        request.Fields.Contains(field, StringComparer.OrdinalIgnoreCase);

    /// <summary>Call ValidateAsync first.</summary>
    public async Task<BulkEditAccountProgressResult> ApplyAsync(BulkEditAccountProgressRequest request, int userKey)
    {
        var keys = request.AccountKeys.Distinct().ToList();
        int updated = 0, unchanged = 0;
        var skipped = new List<BulkEditSkippedAccount>();
        var batchId = Guid.NewGuid().ToString("N")[..8];

        foreach (var accountKey in keys)
        {
            var before = await repository.GetDetailAsync(accountKey);
            if (before is null)
            {
                skipped.Add(new BulkEditSkippedAccount { AccountKey = accountKey, Reason = "Account not found." });
                continue;
            }

            // Already open by this same user (another tab): save, but leave
            // their lock for that open form to release.
            var alreadyHeld = await lockRepository.IsHeldByAsync(accountKey, userKey);
            var lockStatus = alreadyHeld ? null : await lockRepository.TryAcquireAsync(accountKey, userKey);
            if (!alreadyHeld && (lockStatus is null || lockStatus.LockedByUserKey != userKey))
            {
                skipped.Add(new BulkEditSkippedAccount
                {
                    AccountKey = accountKey, AccountName = before.AccountName,
                    Reason = $"Being edited by {lockStatus?.LockedByName ?? "another user"}."
                });
                continue;
            }

            try
            {
                var save = BuildRequest(before, request);
                var result = await saveService.SaveAsync(accountKey, before, save, userKey, keepUnchangedException: true,
                    auditDetail: $"Bulk edit {batchId}");
                if (!result.Saved)
                    skipped.Add(new BulkEditSkippedAccount { AccountKey = accountKey, AccountName = before.AccountName, Reason = result.Error! });
                else if (result.Changed)
                    updated++;
                else
                    unchanged++;
            }
            catch (Exception ex)
            {
                skipped.Add(new BulkEditSkippedAccount { AccountKey = accountKey, AccountName = before.AccountName, Reason = $"Could not save: {ex.Message}" });
            }
            finally
            {
                if (!alreadyHeld) await lockRepository.ReleaseAsync(accountKey, userKey);
            }
        }

        await auditLogger.LogAsync("BulkEdit", userKey, "fact_account_progress", entityKey: null,
            detail: $"Bulk edit {batchId}: {string.Join(", ", request.Fields)} on {keys.Count} accounts -- {updated} updated, {unchanged} unchanged, {skipped.Count} skipped",
            reason: string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason);

        return new BulkEditAccountProgressResult { Requested = keys.Count, Updated = updated, Unchanged = unchanged, Skipped = skipped };
    }

    /// <summary>The account's current values, with the chosen fields replaced.</summary>
    private static SaveAccountProgressRequest BuildRequest(AccountProgressDetail before, BulkEditAccountProgressRequest bulk)
    {
        bool Has(string field) => AccountProgressBulkEditService.Has(bulk, field);
        string? notes = before.Notes;
        if (Has("Notes"))
        {
            notes = bulk.NotesMode == "Append"
                ? string.IsNullOrWhiteSpace(before.Notes) ? bulk.Notes!.Trim() : $"{before.Notes.TrimEnd()}\n{bulk.Notes!.Trim()}"
                : string.IsNullOrWhiteSpace(bulk.Notes) ? null : bulk.Notes;
        }
        string? Text(string field, string? current, string? value) => Has(field) ? (string.IsNullOrWhiteSpace(value) ? null : value.Trim()) : current;

        return new SaveAccountProgressRequest
        {
            CurrentStageKey = Has("CurrentStageKey") ? bulk.CurrentStageKey!.Value : before.CurrentStageKey,
            CurrentStatusKey = Has("CurrentStatusKey") ? bulk.CurrentStatusKey!.Value : before.CurrentStatusKey,
            RiskLevelKey = Has("RiskLevelKey") ? bulk.RiskLevelKey : before.RiskLevelKey,
            AccountTypeKey = Has("AccountTypeKey") ? bulk.AccountTypeKey : before.AccountTypeKey,
            SORKey = Has("SORKey") ? bulk.SORKey : before.SORKey,
            OwnerName = Text("OwnerName", before.OwnerName, bulk.OwnerName),
            BusinessUnit = Text("BusinessUnit", before.BusinessUnit, bulk.BusinessUnit),
            TargetRemediationDate = Has("TargetRemediationDate") ? bulk.TargetRemediationDate : before.TargetRemediationDate,
            ActualCompletionDate = Has("ActualCompletionDate") ? bulk.ActualCompletionDate : before.ActualCompletionDate,
            Notes = notes,
            Reason = bulk.Reason,
            ExceptionKey = before.ExceptionKey
        };
    }
}
