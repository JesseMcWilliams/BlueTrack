using BlueTrack.Api.Audit;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.AccountProgress;

/// <summary>
/// D-185: delete and undelete accounts in BlueTrack, one or many (the
/// Account Progress selection), always with a reason. Agreed 2026-10-09:
///   - A BlueTrack delete survives the nightly load; undelete reverses
///     BlueTrack deletes only (a CyberArk deletion comes back each night).
///   - Each account is skipped and reported if someone else is editing it,
///     if it's already in the requested state, or (undelete) if only
///     CyberArk deleted it. One account's problem doesn't stop the rest.
///   - At most app_config.BulkEditMaxAccounts accounts per request.
///   - Each change is an AccountDeleted / AccountUndeleted audit event with
///     the reason, plus a web.account_deletion_history row.
/// </summary>
public sealed class AccountDeletionService(
    AccountDeletionRepository deletionRepository,
    AccountProgressLockRepository lockRepository,
    AppConfigRepository appConfigRepository,
    AuditLogger auditLogger)
{
    public const int MaxReasonLength = 1000;

    /// <summary>Null when usable; otherwise why not (a 400 for the whole request).</summary>
    public async Task<string?> ValidateAsync(AccountDeletionRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Reason)) return "A reason is required.";
        if (request.Reason.Trim().Length > MaxReasonLength) return $"The reason must be {MaxReasonLength} characters or fewer.";
        var count = request.AccountKeys?.Distinct().Count() ?? 0;
        if (count == 0) return "Select at least one account.";
        var max = (await appConfigRepository.GetAsync()).BulkEditMaxAccounts;
        if (count > max) return $"At most {max} accounts can be changed at once; {count} were selected.";
        return null;
    }

    public Task<AccountDeletionResult> DeleteAsync(AccountDeletionRequest request, int userKey) => ApplyAsync(request, userKey, delete: true);

    public Task<AccountDeletionResult> UndeleteAsync(AccountDeletionRequest request, int userKey) => ApplyAsync(request, userKey, delete: false);

    private async Task<AccountDeletionResult> ApplyAsync(AccountDeletionRequest request, int userKey, bool delete)
    {
        var keys = request.AccountKeys.Distinct().ToList();
        var reason = request.Reason.Trim();
        var batchId = keys.Count > 1 ? Guid.NewGuid().ToString("N")[..8] : null;
        var changed = 0;
        var skipped = new List<BulkEditSkippedAccount>();

        foreach (var accountKey in keys)
        {
            var state = await deletionRepository.GetStateAsync(accountKey);
            BulkEditSkippedAccount Skip(string why) => new() { AccountKey = accountKey, AccountName = state?.AccountName, Reason = why };
            if (state is null) { skipped.Add(Skip("Account not found.")); continue; }
            if (delete && state.DeletedInBlueTrack) { skipped.Add(Skip("Already deleted in BlueTrack.")); continue; }
            if (!delete && !state.DeletedInBlueTrack)
            {
                skipped.Add(Skip(state.IsDeletedInSource
                    ? "Deleted in CyberArk, not in BlueTrack; only a BlueTrack delete can be undone."
                    : "Not deleted."));
                continue;
            }

            var alreadyHeld = await lockRepository.IsHeldByAsync(accountKey, userKey);
            var lockStatus = alreadyHeld ? null : await lockRepository.TryAcquireAsync(accountKey, userKey);
            if (!alreadyHeld && (lockStatus is null || lockStatus.LockedByUserKey != userKey))
            {
                skipped.Add(Skip($"Being edited by {lockStatus?.LockedByName ?? "another user"}."));
                continue;
            }

            try
            {
                if (delete) await deletionRepository.DeleteAsync(accountKey, reason, userKey, batchId);
                else await deletionRepository.UndeleteAsync(accountKey, reason, userKey, batchId);
                await auditLogger.LogAsync(delete ? "AccountDeleted" : "AccountUndeleted", userKey, "fact_account", accountKey.ToString(),
                    detail: batchId is null ? null : $"Batch {batchId}", reason: reason);
                changed++;
            }
            catch (Exception ex)
            {
                skipped.Add(Skip($"Could not save: {ex.Message}"));
            }
            finally
            {
                if (!alreadyHeld) await lockRepository.ReleaseAsync(accountKey, userKey);
            }
        }

        return new AccountDeletionResult { Requested = keys.Count, Changed = changed, Skipped = skipped };
    }
}
