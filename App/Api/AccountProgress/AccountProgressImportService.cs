using System.Globalization;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.AccountProgress;

/// <summary>
/// D-318: reads back an Accounts export (CSV) with changed values. "Account Key" finds each account; each editable
/// column the file has is applied to that account as one bulk edit of those fields, through the same rules as the
/// edit page (AccountProgressBulkEditService.BuildRequest and AccountProgressSaveService): an account someone is
/// editing is skipped, a rule that refuses a row doesn't stop the others, and each change is audited per account.
/// A blank cell leaves the field as it is; a value is matched to its list by name, ignoring case. The identity
/// columns (Username, Address, Account Name, Safe) are only for reading and are ignored.
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

    private static readonly string[] DateFormats = ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "M/d/yyyy", "d/M/yyyy"];

    /// <summary>Null when the file can be imported; otherwise why not (a 400 for the whole file).</summary>
    public async Task<string?> ValidateAsync(IReadOnlyList<IReadOnlyDictionary<string, string>> rows)
    {
        if (rows.Count == 0) return "The file has no rows.";
        var headers = rows[0].Keys.Select(h => h.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!headers.Contains(KeyColumn)) return $"The file needs an \"{KeyColumn}\" column (export the Accounts list to get one).";
        if (!Columns.Any(c => headers.Contains(c.Header))) return "The file has none of the editable columns (Stage, Status, Owner, ...).";
        var max = (await appConfigRepository.GetAsync()).BulkEditMaxAccounts;
        return rows.Count > max ? $"An import can change at most {max} accounts; the file has {rows.Count} rows." : null;
    }

    /// <summary>Call ValidateAsync first. <paramref name="reason"/> is used where a row moves an account to an earlier stage (D-51).</summary>
    public async Task<AccountProgressImportResult> ImportAsync(
        IReadOnlyList<IReadOnlyDictionary<string, string>> rows, string fileName, string? reason, int userKey)
    {
        var lists = await referenceDataRepository.GetAllReferenceDataAsync();
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

            var (request, error) = BuildRequest(row, accountKey, lists, reason);
            if (error is not null)
            {
                problems.Add(new AccountProgressImportProblem { RowNumber = rowNumber, AccountKey = accountKey, Reason = error });
                continue;
            }
            if (request!.Fields.Count == 0)
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
                var save = AccountProgressBulkEditService.BuildRequest(before, request);
                var result = await saveService.SaveAsync(accountKey, before, save, userKey, keepUnchangedException: true, auditDetail: $"Import {batchId}");
                if (!result.Saved) problems.Add(new AccountProgressImportProblem { RowNumber = rowNumber, AccountKey = accountKey, Reason = result.Error! });
                else if (result.Changed) updated++;
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
            detail: AuditLogger.ClipDetail($"Import {batchId} from {fileName}: {rows.Count} rows -- {updated} updated, {unchanged} unchanged, {problems.Count} not applied"),
            reason: string.IsNullOrWhiteSpace(reason) ? null : reason);

        return new AccountProgressImportResult { Rows = rows.Count, Updated = updated, Unchanged = unchanged, Problems = problems };
    }

    /// <summary>The bulk edit one row asks for: only its non-blank editable cells, each turned into a key, a date or text.</summary>
    private static (BulkEditAccountProgressRequest? Request, string? Error) BuildRequest(
        IReadOnlyDictionary<string, string> row, long accountKey, IReadOnlyDictionary<string, IReadOnlyList<DropdownOption>> lists, string? reason)
    {
        var fields = new List<string>();
        var keys = new Dictionary<string, int>();
        var texts = new Dictionary<string, string>();
        var dates = new Dictionary<string, DateTime>();
        foreach (var (header, field, list) in Columns)
        {
            var value = row.GetValueOrDefault(header)?.Trim();
            if (string.IsNullOrEmpty(value)) continue;
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
            CurrentStageKey = keys.TryGetValue("CurrentStageKey", out var stage) ? stage : null,
            CurrentStatusKey = keys.TryGetValue("CurrentStatusKey", out var status) ? status : null,
            RiskLevelKey = keys.TryGetValue("RiskLevelKey", out var risk) ? risk : null,
            AccountTypeKey = keys.TryGetValue("AccountTypeKey", out var type) ? type : null,
            SORKey = keys.TryGetValue("SORKey", out var sor) ? sor : null,
            ApplicationKey = keys.TryGetValue("ApplicationKey", out var application) ? application : null,
            OwnerName = texts.GetValueOrDefault("OwnerName"),
            BusinessUnit = texts.GetValueOrDefault("BusinessUnit"),
            TargetRemediationDate = dates.TryGetValue("TargetRemediationDate", out var target) ? target : null,
            ActualCompletionDate = dates.TryGetValue("ActualCompletionDate", out var actual) ? actual : null,
            Notes = texts.GetValueOrDefault("Notes"),
            Reason = string.IsNullOrWhiteSpace(reason) ? null : reason
        }, null);
    }
}
