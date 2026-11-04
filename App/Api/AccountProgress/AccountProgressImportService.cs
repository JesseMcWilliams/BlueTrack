using System.Globalization;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.AccountProgress;

/// <summary>
/// D-318: reads back an Accounts export (CSV, or D-323 an Excel workbook) with changed values. "Account Key" finds
/// each account; each editable column the file has is applied to that account as one bulk edit of those fields,
/// through the same rules as the edit page (AccountProgressBulkEditService.BuildRequest and
/// AccountProgressSaveService): an account someone is editing is skipped, a rule that refuses a row doesn't stop the
/// others, and each change is audited per account. A value is matched to its list by name, ignoring case. The
/// identity columns (Username, Address, Account Name, Safe) are only for reading and are ignored.
/// A blank cell leaves the field as it is, unless D-323's "clear blanks" is chosen: then a blank cell in a column the
/// file has clears that field (not Stage or Status, which always have a value). D-323 also reads "Risk Score Override"
/// and "Override Reason": a score sets the override (with the row's reason, else the form's; one is needed, as on
/// the account page), and with "clear blanks" a blank score clears it.
/// </summary>
public sealed class AccountProgressImportService(
    AccountProgressRepository repository,
    AccountProgressLockRepository lockRepository,
    AccountProgressSaveService saveService,
    ReferenceDataRepository referenceDataRepository,
    AppConfigRepository appConfigRepository,
    AuditLogger auditLogger,
    ILogger<AccountProgressImportService> logger)
{
    public const string KeyColumn = "Account Key";
    public const string OverrideColumn = "Risk Score Override";
    public const string OverrideReasonColumn = "Override Reason";

    // Header -> the bulk edit field it sets, and the reference list its names come from (null: free text or a date).
    private static readonly (string Header, string Field, string? List)[] Columns =
    [
        ("Stage", "CurrentStageKey", "dim_blueprint_stage"),
        ("Status", "CurrentStatusKey", "dim_progress_status"),
        ("Risk Level", "RiskLevelKey", "dim_risk_level"),
        ("Account Type", "AccountTypeKey", "dim_account_type"),
        ("Source of Record", "SORKey", "dim_source_of_record"),
        ("Application", "ApplicationKey", "dim_application"),
        ("Owner", "OwnerName", null),
        ("Business Unit", "BusinessUnit", null),
        ("Target Remediation Date", "TargetRemediationDate", null),
        ("Actual Completion Date", "ActualCompletionDate", null),
        ("Notes", "Notes", null)
    ];

    // Always have a value, so a blank cell never clears them.
    private static readonly HashSet<string> Required = ["CurrentStageKey", "CurrentStatusKey"];

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "M/d/yyyy", "d/M/yyyy"];

    /// <summary>Null when the file can be imported; otherwise why not (a 400 for the whole file).</summary>
    public async Task<string?> ValidateAsync(IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        if (rows.Count == 0) return "The file has no rows.";
        var headers = rows[0].Keys.Select(h => h.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!headers.Contains(KeyColumn)) return $"The file needs an \"{KeyColumn}\" column (export the Accounts list to get one).";
        if (!Columns.Any(c => headers.Contains(c.Header)) && !headers.Contains(OverrideColumn))
            return "The file has none of the editable columns (Stage, Status, Owner, Risk Score Override, ...).";
        var max = (await appConfigRepository.GetAsync()).BulkEditMaxAccounts;
        return rows.Count > max ? $"An import can change at most {max} accounts; the file has {rows.Count} rows." : null;
    }

    /// <summary>
    /// Call ValidateAsync first. <paramref name="reason"/> is used where a row moves an account to an earlier stage
    /// (D-51), and for an override whose row has no reason. <paramref name="clearBlanks"/>: a blank cell clears its field.
    /// </summary>
    public async Task<AccountProgressImportResult> ImportAsync(
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows, string fileName, string? reason, bool clearBlanks, int userKey)
    {
        var lists = await referenceDataRepository.GetAllReferenceDataAsync();
        var headers = rows[0].Keys.Select(h => h.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var batchId = Guid.NewGuid().ToString("N")[..8];
        int updated = 0, unchanged = 0;
        var problems = new List<AccountProgressImportProblem>();

        for (var i = 0; i < rows.Count; i++)
        {
            var rowNumber = i + 2;   // the header is row 1
            var row = rows[i].ToDictionary(kv => kv.Key.Trim(), kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            if (!long.TryParse(row.GetValueOrDefault(KeyColumn)?.Trim(), out var accountKey))
            {
                problems.Add(new AccountProgressImportProblem { RowNumber = rowNumber, Reason = $"\"{KeyColumn}\" isn't a number." });
                continue;
            }

            var (request, error) = BuildRequest(row, headers, accountKey, lists, reason, clearBlanks);
            var (overrideChange, overrideError) = ReadOverride(row, headers, reason, clearBlanks);
            if ((error ?? overrideError) is { } rowError)
            {
                problems.Add(new AccountProgressImportProblem { RowNumber = rowNumber, AccountKey = accountKey, Reason = rowError });
                continue;
            }
            if (request!.Fields.Count == 0 && overrideChange is null)
            {
                unchanged++;
                continue;
            }

            var before = await repository.GetDetailAsync(accountKey);
            if (before is null)
            {
                problems.Add(new AccountProgressImportProblem { RowNumber = rowNumber, AccountKey = accountKey, Reason = "No account has this key." });
                continue;
            }

            var alreadyHeld = await lockRepository.IsHeldByAsync(accountKey, userKey);
            var lockStatus = alreadyHeld ? null : await lockRepository.TryAcquireAsync(accountKey, userKey);
            if (!alreadyHeld && (lockStatus is null || lockStatus.LockedByUserKey != userKey))
            {
                problems.Add(new AccountProgressImportProblem { RowNumber = rowNumber, AccountKey = accountKey, Reason = $"Being edited by {lockStatus?.LockedByName ?? "another user"}." });
                continue;
            }

            try
            {
                var changed = false;
                if (request.Fields.Count > 0)
                {
                    var save = AccountProgressBulkEditService.BuildRequest(before, request);
                    var result = await saveService.SaveAsync(accountKey, before, save, userKey, keepUnchangedException: true, auditDetail: $"Import {batchId}");
                    if (!result.Saved)
                    {
                        problems.Add(new AccountProgressImportProblem { RowNumber = rowNumber, AccountKey = accountKey, Reason = result.Error! });
                        continue;
                    }
                    changed = result.Changed;
                }
                if (overrideChange is { } change)
                {
                    var current = await repository.GetRiskScoreOverrideAsync(accountKey);
                    if (current.OverrideRiskScore != change.Score)
                    {
                        await repository.SetRiskScoreOverrideAsync(accountKey, change.Score, change.Reason, userKey);
                        await auditLogger.LogAsync("FieldEdit", userKey, "account_risk_score", accountKey.ToString(), reason: change.Reason,
                            detail: $"Import {batchId}",
                            fieldChanges: [new FieldChange("OverrideRiskScore", current.OverrideRiskScore?.ToString(), change.Score?.ToString())]);
                        changed = true;
                    }
                }
                if (changed) updated++;
                else unchanged++;
            }
            catch (Exception ex)
            {
                problems.Add(new AccountProgressImportProblem { RowNumber = rowNumber, AccountKey = accountKey, Reason = $"Could not save: {Errors.RowErrorText.For(ex, logger)}" });
            }
            finally
            {
                if (!alreadyHeld) await lockRepository.ReleaseAsync(accountKey, userKey);
            }
        }

        await auditLogger.LogAsync("BulkEdit", userKey, "fact_account_progress", entityKey: null,
            detail: AuditLogger.ClipDetail($"Import {batchId} from {fileName}{(clearBlanks ? " (blank cells clear)" : "")}: {rows.Count} rows -- {updated} updated, {unchanged} unchanged, {problems.Count} not applied"),
            reason: string.IsNullOrWhiteSpace(reason) ? null : reason);

        return new AccountProgressImportResult { Rows = rows.Count, Updated = updated, Unchanged = unchanged, Problems = problems };
    }

    /// <summary>
    /// The bulk edit one row asks for: its non-blank editable cells, each turned into a key, a date or text, and with
    /// <paramref name="clearBlanks"/> its blank cells in the file's columns, as null (except Stage and Status).
    /// </summary>
    private static (BulkEditAccountProgressRequest? Request, string? Error) BuildRequest(
        IReadOnlyDictionary<string, string> row, IReadOnlySet<string> headers, long accountKey,
        IReadOnlyDictionary<string, IReadOnlyList<DropdownOption>> lists, string? reason, bool clearBlanks)
    {
        var fields = new List<string>();
        var keys = new Dictionary<string, int?>();
        var texts = new Dictionary<string, string?>();
        var dates = new Dictionary<string, DateTime?>();
        foreach (var (header, field, list) in Columns)
        {
            if (!headers.Contains(header)) continue;
            var value = row.GetValueOrDefault(header)?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                if (!clearBlanks || Required.Contains(field)) continue;
                if (list is not null) keys[field] = null;
                else if (field.EndsWith("Date", StringComparison.Ordinal)) dates[field] = null;
                else texts[field] = null;
                fields.Add(field);
                continue;
            }
            if (list is not null)
            {
                var match = lists.GetValueOrDefault(list)?.FirstOrDefault(o => string.Equals(o.Name, value, StringComparison.OrdinalIgnoreCase));
                if (match is null) return (null, $"{header} \"{value}\" isn't one of the choices.");
                keys[field] = match.Key;
            }
            else if (field.EndsWith("Date", StringComparison.Ordinal))
            {
                if (!DateTime.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
                    return (null, $"{header} \"{value}\" isn't a date (use yyyy-MM-dd).");
                dates[field] = date;
            }
            else
            {
                texts[field] = value;
            }
            fields.Add(field);
        }

        return (new BulkEditAccountProgressRequest
        {
            AccountKeys = [accountKey],
            Fields = fields,
            CurrentStageKey = keys.GetValueOrDefault("CurrentStageKey"),
            CurrentStatusKey = keys.GetValueOrDefault("CurrentStatusKey"),
            RiskLevelKey = keys.GetValueOrDefault("RiskLevelKey"),
            AccountTypeKey = keys.GetValueOrDefault("AccountTypeKey"),
            SORKey = keys.GetValueOrDefault("SORKey"),
            ApplicationKey = keys.GetValueOrDefault("ApplicationKey"),
            OwnerName = texts.GetValueOrDefault("OwnerName"),
            BusinessUnit = texts.GetValueOrDefault("BusinessUnit"),
            TargetRemediationDate = dates.GetValueOrDefault("TargetRemediationDate"),
            ActualCompletionDate = dates.GetValueOrDefault("ActualCompletionDate"),
            Notes = texts.GetValueOrDefault("Notes"),
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason
        }, null);
    }

    /// <summary>D-323: the override a row asks for: a score (0-1000, with a reason), a clear (blank, with clearBlanks), or nothing.</summary>
    private static ((int? Score, string? Reason)? Change, string? Error) ReadOverride(
        IReadOnlyDictionary<string, string> row, IReadOnlySet<string> headers, string? formReason, bool clearBlanks)
    {
        if (!headers.Contains(OverrideColumn)) return (null, null);
        var value = row.GetValueOrDefault(OverrideColumn)?.Trim();
        var rowReason = row.GetValueOrDefault(OverrideReasonColumn)?.Trim();
        var reason = string.IsNullOrEmpty(rowReason) ? (string.IsNullOrWhiteSpace(formReason) ? null : formReason.Trim()) : rowReason;
        if (string.IsNullOrEmpty(value)) return clearBlanks ? ((null, reason), null) : (null, null);
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var score) || score is < 0 or > 1000)
            return (null, $"{OverrideColumn} \"{value}\" isn't a whole number from 0 to 1000.");
        if (reason is null) return (null, $"{OverrideColumn} needs a reason: fill in {OverrideReasonColumn}, or give one on the import form.");
        return ((score, reason), null);
    }
}
