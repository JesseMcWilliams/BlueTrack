using System.Globalization;
using BlueTrack.Api.AccountProgress;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Imports;

/// <summary>
/// D-183: the Risk Exceptions CSV import, for exceptions approved in another
/// tool. Agreed 2026-10-09:
///   - Each row becomes a new exception with a BlueTrack ExceptionID (the
///     configured pattern), keeping the source's tool, ID and link.
///   - SourceTool + SourceExceptionId already imported is a row error (no update).
///   - The approver is the source's, by name (ApprovedByName); no BlueTrack
///     user is recorded as approver. The importer is recorded as ImportedBy.
///   - Scope: an account by AccountUserName + AccountAddress (exact match,
///     ignoring case and surrounding spaces; both required, so a blank
///     address matches nothing) or an application by
///     ApplicationCode -- exactly one.
///   - Status column: Active / Expired / Revoked; blank means Active.
///   - LinkToAccountProgress = Yes also links an Active account exception to
///     that account's progress record (status Risk Accepted / Excluded),
///     through the edit page's own save rules.
/// Per-row errors; a bad row doesn't stop the rest. Dates are yyyy-MM-dd.
/// </summary>
public sealed class RiskExceptionImportService(
    RiskExceptionRepository exceptionRepository,
    ApplicationRepository applicationRepository,
    AccountProgressRepository progressRepository,
    AccountProgressLockRepository lockRepository,
    AccountProgressSaveService saveService,
    AuditLogger auditLogger)
{
    public static readonly string[] Columns =
    [
        "SourceTool", "SourceExceptionId", "SourceUrl", "AccountUserName", "AccountAddress", "ApplicationCode",
        "Justification", "ApprovedByName", "ApprovalDate", "ReviewDate", "Status", "ExternalTicketReference", "LinkToAccountProgress"
    ];

    // web.risk_exception column sizes (08_ and 45_ scripts), checked per row.
    private static readonly Dictionary<string, int> MaxLengths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SourceTool"] = 100, ["SourceExceptionId"] = 100, ["SourceUrl"] = 1000, ["Justification"] = 2000,
        ["ApprovedByName"] = 200, ["ExternalTicketReference"] = 200
    };

    private const string RiskAcceptedStatus = "Risk Accepted / Excluded";

    public async Task<RiskExceptionImportResult> ImportAsync(IReadOnlyList<IReadOnlyDictionary<string, string>> rows, int userKey)
    {
        var applications = await applicationRepository.GetAllDetailedAsync();
        var riskAcceptedKey = await progressRepository.GetStatusKeyAsync(RiskAcceptedStatus);
        int created = 0, linked = 0;
        var errors = new List<ImportRowError>();
        var createdIds = new List<ImportedExceptionId>();

        for (var i = 0; i < rows.Count; i++)
        {
            var rowNumber = i + 2; // header is row 1
            var row = rows[i];
            string? Cell(string column)
            {
                var value = row.TryGetValue(column, out var raw) ? raw.Trim() : "";
                if (MaxLengths.TryGetValue(column, out var max) && value.Length > max)
                    throw new InvalidOperationException($"{column} is longer than {max} characters.");
                return value.Length == 0 ? null : value;
            }

            ImportedRiskException exception;
            bool link;
            try
            {
                var sourceTool = Cell("SourceTool") ?? throw new InvalidOperationException("SourceTool is required.");
                var sourceId = Cell("SourceExceptionId") ?? throw new InvalidOperationException("SourceExceptionId is required.");
                var sourceUrl = Cell("SourceUrl");
                if (sourceUrl is not null && !(Uri.TryCreate(sourceUrl, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)))
                    throw new InvalidOperationException("SourceUrl must be a full http:// or https:// link.");

                if (await exceptionRepository.FindBySourceAsync(sourceTool, sourceId) is { } existing)
                    throw new InvalidOperationException($"{sourceTool} exception {sourceId} was already imported, as {existing}.");

                var userName = Cell("AccountUserName");
                var address = Cell("AccountAddress");
                var applicationCode = Cell("ApplicationCode");
                var isAccount = userName is not null || address is not null;
                if (isAccount == (applicationCode is not null))
                    throw new InvalidOperationException("Give either AccountUserName + AccountAddress, or ApplicationCode -- exactly one scope.");

                long? accountKey = null;
                int? applicationKey = null;
                if (isAccount)
                {
                    if (userName is null || address is null)
                        throw new InvalidOperationException("An account needs both AccountUserName and AccountAddress; a blank address isn't matched.");
                    var matches = await exceptionRepository.FindAccountKeysAsync(userName, address);
                    accountKey = matches.Count switch
                    {
                        0 => throw new InvalidOperationException($"No account with username '{userName}' and address '{address}'."),
                        1 => matches[0],
                        _ => throw new InvalidOperationException($"{matches.Count} accounts have username '{userName}' and address '{address}'; can't tell which.")
                    };
                }
                else
                {
                    applicationKey = applications.FirstOrDefault(a => string.Equals(a.ApplicationCode, applicationCode, StringComparison.OrdinalIgnoreCase))?.ApplicationKey
                        ?? throw new InvalidOperationException($"No application with code '{applicationCode}'.");
                }

                var justification = Cell("Justification") ?? throw new InvalidOperationException("Justification is required.");
                var approvedBy = Cell("ApprovedByName") ?? throw new InvalidOperationException("ApprovedByName is required.");
                var approvalDate = ParseDate(Cell("ApprovalDate"), "ApprovalDate");
                var reviewDate = ParseDate(Cell("ReviewDate"), "ReviewDate");
                if (reviewDate < approvalDate) throw new InvalidOperationException("ReviewDate is before ApprovalDate.");

                var status = Cell("Status") ?? "Active";
                status = new[] { "Active", "Expired", "Revoked" }.FirstOrDefault(s => string.Equals(s, status, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException("Status must be Active, Expired or Revoked (blank means Active).");

                link = ParseYesNo(Cell("LinkToAccountProgress"));
                if (link && !isAccount) throw new InvalidOperationException("LinkToAccountProgress applies to account exceptions only.");
                if (link && status != "Active") throw new InvalidOperationException("Only an Active exception can be linked to Account Progress.");

                exception = new ImportedRiskException
                {
                    AccountKey = accountKey, ApplicationKey = applicationKey, Justification = justification, ApprovedByName = approvedBy,
                    ApprovalDate = approvalDate, ReviewDate = reviewDate, StatusName = status,
                    ExternalTicketReference = Cell("ExternalTicketReference"),
                    SourceTool = sourceTool, SourceExceptionId = sourceId, SourceUrl = sourceUrl
                };
            }
            catch (InvalidOperationException ex)
            {
                errors.Add(new ImportRowError { RowNumber = rowNumber, Error = ex.Message });
                continue;
            }

            int exceptionKey;
            string exceptionId;
            try
            {
                (exceptionKey, exceptionId) = await exceptionRepository.CreateImportedAsync(exception, userKey);
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
            {
                // The same source ID twice in one file: the unique index catches the second.
                errors.Add(new ImportRowError { RowNumber = rowNumber, Error = $"{exception.SourceTool} exception {exception.SourceExceptionId} was already imported." });
                continue;
            }
            created++;
            createdIds.Add(new ImportedExceptionId { RowNumber = rowNumber, ExceptionId = exceptionId, SourceExceptionId = exception.SourceExceptionId });

            await auditLogger.LogAsync("ExceptionImported", userKey, "risk_exception", exceptionKey.ToString(),
                detail: $"Imported from {exception.SourceTool} {exception.SourceExceptionId} as {exceptionId}, approved there by {exception.ApprovedByName}",
                fieldChanges:
                [
                    new FieldChange("Justification", null, exception.Justification),
                    new FieldChange("ApprovalDate", null, exception.ApprovalDate.ToString("yyyy-MM-dd")),
                    new FieldChange("ReviewDate", null, exception.ReviewDate.ToString("yyyy-MM-dd")),
                    new FieldChange("Status", null, exception.StatusName),
                    new FieldChange("SourceUrl", null, exception.SourceUrl)
                ]);

            if (link)
            {
                var linkError = await LinkAsync(exception.AccountKey!.Value, exceptionKey, riskAcceptedKey, userKey);
                if (linkError is null) linked++;
                else errors.Add(new ImportRowError { RowNumber = rowNumber, Error = $"Imported as {exceptionId}, but not linked to Account Progress: {linkError}" });
            }
        }

        return new RiskExceptionImportResult
        {
            TotalRows = rows.Count, CreatedCount = created, LinkedCount = linked, Created = createdIds, Errors = errors
        };
    }

    /// <summary>Sets the account's status to Risk Accepted / Excluded with this exception, as the edit page would. Null on success.</summary>
    private async Task<string?> LinkAsync(long accountKey, int exceptionKey, int? riskAcceptedKey, int userKey)
    {
        if (riskAcceptedKey is null) return $"the '{RiskAcceptedStatus}' status doesn't exist.";
        var before = await progressRepository.GetDetailAsync(accountKey);
        if (before is null) return "the account has no progress record.";

        var alreadyHeld = await lockRepository.IsHeldByAsync(accountKey, userKey);
        var lockStatus = alreadyHeld ? null : await lockRepository.TryAcquireAsync(accountKey, userKey);
        if (!alreadyHeld && (lockStatus is null || lockStatus.LockedByUserKey != userKey))
            return $"the account is being edited by {lockStatus?.LockedByName ?? "another user"}.";
        try
        {
            var result = await saveService.SaveAsync(accountKey, before, new SaveAccountProgressRequest
            {
                CurrentStageKey = before.CurrentStageKey, CurrentStatusKey = riskAcceptedKey.Value, RiskLevelKey = before.RiskLevelKey,
                AccountTypeKey = before.AccountTypeKey, SORKey = before.SORKey, OwnerName = before.OwnerName, BusinessUnit = before.BusinessUnit,
                TargetRemediationDate = before.TargetRemediationDate, ActualCompletionDate = before.ActualCompletionDate, Notes = before.Notes,
                ExceptionKey = exceptionKey
            }, userKey, auditDetail: "Linked by the Risk Exceptions import");
            return result.Saved ? null : result.Error;
        }
        finally
        {
            if (!alreadyHeld) await lockRepository.ReleaseAsync(accountKey, userKey);
        }
    }

    private static DateTime ParseDate(string? value, string column) =>
        value is null ? throw new InvalidOperationException($"{column} is required.")
        : DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date
        : throw new InvalidOperationException($"{column} must be a date as yyyy-MM-dd, e.g. 2026-10-09.");

    private static bool ParseYesNo(string? value) => value?.ToUpperInvariant() switch
    {
        null or "NO" or "N" or "FALSE" or "0" => false,
        "YES" or "Y" or "TRUE" or "1" => true,
        _ => throw new InvalidOperationException("LinkToAccountProgress must be Yes or No (blank means No).")
    };
}
