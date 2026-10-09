using BlueTrack.Api.Audit;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Imports;

/// <summary>
/// D-180's two CSV imports for Application ↔ Safe Mapping, moved out of
/// ApplicationMappingImportController unchanged (Data Sources phase 1,
/// Planning_Data-Sources.md) so the Bulk Actions uploads and the scheduled
/// data feeds (phase 2) run the same code. Each method takes already-parsed
/// CSV rows (CsvFileReader).
///   - Applications: ApplicationCode identifies the row. A new code creates
///     the application; an existing code updates it, and a blank optional
///     cell leaves that field as it is (a partial file never clears data).
///   - Safe assignments: SafeName + Application (code, or else name), with
///     an optional Source. A name with no Source assigns every safe of that
///     name, in every source. An existing assignment is overwritten. Safes
///     not in the file are never touched.
/// Every change is audit-logged exactly as the page's own one-at-a-time
/// edits are (ApplicationsController, SafesController).
/// </summary>
public sealed class ApplicationMappingImportService(
    ApplicationRepository repository,
    AuditLogger auditLogger)
{
    public static readonly string[] ApplicationColumns =
        ["ApplicationCode", "ApplicationName", "Description", "OwnerName", "OwnerEmail", "TechnicalName", "TechnicalEmail", "Notes"];

    // web.dim_application column sizes (08_BlueTrack_WebSchema.sql), checked
    // per row so an over-long value is a clear row error, not a truncation exception.
    private static readonly Dictionary<string, int> MaxLengths = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ApplicationCode"] = 50, ["ApplicationName"] = 300, ["Description"] = 1000, ["OwnerName"] = 300,
        ["OwnerEmail"] = 320, ["TechnicalName"] = 300, ["TechnicalEmail"] = 320, ["Notes"] = 2000
    };

    // Source column: short names, or dim_source_system's own names.
    private static readonly Dictionary<string, string> SourceAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PrivilegeCloud"] = "CyberArk Privilege Cloud (SaaS)",
        ["SelfHosted"] = "CyberArk Self-Hosted Vault"
    };

    public async Task<ApplicationImportResult> ImportApplicationsAsync(IReadOnlyList<IReadOnlyDictionary<string, string>> rows, int userKey)
    {
        var applications = (await repository.GetAllDetailedAsync()).ToList();
        int created = 0, updated = 0, unchanged = 0;
        var errors = new List<ImportRowError>();

        for (var i = 0; i < rows.Count; i++)
        {
            var rowNumber = i + 2; // header is row 1
            try
            {
                var row = rows[i];
                string? Cell(string column)
                {
                    var value = row.TryGetValue(column, out var raw) ? raw.Trim() : "";
                    if (value.Length > MaxLengths[column]) throw new InvalidOperationException($"{column} is longer than {MaxLengths[column]} characters.");
                    return value.Length == 0 ? null : value;
                }

                var code = Cell("ApplicationCode") ?? throw new InvalidOperationException("ApplicationCode is required.");
                var name = Cell("ApplicationName");
                var existing = applications.FirstOrDefault(a => string.Equals(a.ApplicationCode, code, StringComparison.OrdinalIgnoreCase));

                var effectiveName = name ?? existing?.ApplicationName ?? throw new InvalidOperationException("ApplicationName is required for a new application.");
                var nameOwner = applications.FirstOrDefault(a => string.Equals(a.ApplicationName, effectiveName, StringComparison.OrdinalIgnoreCase));
                if (nameOwner is not null && nameOwner.ApplicationKey != existing?.ApplicationKey)
                {
                    throw new InvalidOperationException($"ApplicationName '{effectiveName}' is already used by application '{nameOwner.ApplicationCode}'.");
                }

                var request = new SaveApplicationRequest
                {
                    ApplicationCode = existing?.ApplicationCode ?? code,
                    ApplicationName = effectiveName,
                    Description = Cell("Description") ?? existing?.Description,
                    OwnerName = Cell("OwnerName") ?? existing?.OwnerName,
                    OwnerEmail = Cell("OwnerEmail") ?? existing?.OwnerEmail,
                    TechnicalName = Cell("TechnicalName") ?? existing?.TechnicalName,
                    TechnicalEmail = Cell("TechnicalEmail") ?? existing?.TechnicalEmail,
                    Notes = Cell("Notes") ?? existing?.Notes
                };

                if (existing is null)
                {
                    var key = await repository.CreateAsync(request);
                    await auditLogger.LogAsync("FieldEdit", userKey, "dim_application", key.ToString(),
                        detail: $"Application '{request.ApplicationName}' created (CSV import)");
                    applications.Add(ToDetail(key, request));
                    created++;
                }
                else if (SameValues(existing, request))
                {
                    unchanged++;
                }
                else
                {
                    await repository.UpdateAsync(existing.ApplicationKey, request);
                    await auditLogger.LogAsync("FieldEdit", userKey, "dim_application", existing.ApplicationKey.ToString(),
                        detail: $"Application '{request.ApplicationName}' updated (CSV import)");
                    applications[applications.IndexOf(existing)] = ToDetail(existing.ApplicationKey, request);
                    updated++;
                }
            }
            catch (Exception ex)
            {
                errors.Add(new ImportRowError { RowNumber = rowNumber, Error = ex.Message });
            }
        }

        return new ApplicationImportResult
        {
            TotalRows = rows.Count, CreatedCount = created, UpdatedCount = updated, UnchangedCount = unchanged, Errors = errors
        };
    }

    public async Task<SafeAssignmentImportResult> ImportSafeAssignmentsAsync(IReadOnlyList<IReadOnlyDictionary<string, string>> rows, int userKey)
    {
        var applications = await repository.GetAllDetailedAsync();
        var safes = await repository.GetSafesForMatchingAsync();
        int assigned = 0, changed = 0, unchanged = 0;
        var errors = new List<ImportRowError>();

        for (var i = 0; i < rows.Count; i++)
        {
            var rowNumber = i + 2; // header is row 1
            try
            {
                var row = rows[i];
                string? Cell(string column) => row.TryGetValue(column, out var raw) && raw.Trim().Length > 0 ? raw.Trim() : null;

                var safeName = Cell("SafeName") ?? throw new InvalidOperationException("SafeName is required.");
                var applicationText = Cell("Application") ?? throw new InvalidOperationException("Application is required (its code or name).");
                var application =
                    applications.FirstOrDefault(a => string.Equals(a.ApplicationCode, applicationText, StringComparison.OrdinalIgnoreCase))
                    ?? applications.FirstOrDefault(a => string.Equals(a.ApplicationName, applicationText, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"No application with code or name '{applicationText}'. Import it first (Applications import).");

                var sourceText = Cell("Source");
                string? sourceName = null;
                if (sourceText is not null)
                {
                    sourceName = SourceAliases.TryGetValue(sourceText, out var alias) ? alias : sourceText;
                    if (!safes.Any(s => string.Equals(s.SourceSystemName, sourceName, StringComparison.OrdinalIgnoreCase)))
                    {
                        throw new InvalidOperationException($"Source '{sourceText}' is not recognized. Use PrivilegeCloud, SelfHosted, or a source system name.");
                    }
                }

                var matches = safes
                    .Where(s => string.Equals(s.SafeName, safeName, StringComparison.OrdinalIgnoreCase)
                                && (sourceName is null || string.Equals(s.SourceSystemName, sourceName, StringComparison.OrdinalIgnoreCase)))
                    .ToList();
                if (matches.Count == 0)
                {
                    throw new InvalidOperationException(sourceText is null
                        ? $"No safe named '{safeName}'."
                        : $"No safe named '{safeName}' in source '{sourceText}'.");
                }

                foreach (var safe in matches)
                {
                    if (safe.ApplicationKey == application.ApplicationKey)
                    {
                        unchanged++;
                        continue;
                    }
                    var previous = safe.ApplicationKey;
                    await repository.AssignSafeApplicationAsync(safe.SafeKey, application.ApplicationKey);
                    await auditLogger.LogAsync("FieldEdit", userKey, "dim_safe", safe.SafeKey.ToString(),
                        detail: $"ApplicationKey set to {application.ApplicationKey} (CSV import; was {(previous?.ToString() ?? "null")})");
                    safe.ApplicationKey = application.ApplicationKey;
                    if (previous is null) assigned++; else changed++;
                }
            }
            catch (Exception ex)
            {
                errors.Add(new ImportRowError { RowNumber = rowNumber, Error = ex.Message });
            }
        }

        return new SafeAssignmentImportResult
        {
            TotalRows = rows.Count, AssignedCount = assigned, ChangedCount = changed, UnchangedCount = unchanged, Errors = errors
        };
    }

    private static ApplicationDetail ToDetail(int key, SaveApplicationRequest r) => new()
    {
        ApplicationKey = key, ApplicationCode = r.ApplicationCode, ApplicationName = r.ApplicationName, Description = r.Description,
        OwnerName = r.OwnerName, OwnerEmail = r.OwnerEmail, TechnicalName = r.TechnicalName, TechnicalEmail = r.TechnicalEmail, Notes = r.Notes
    };

    private static bool SameValues(ApplicationDetail a, SaveApplicationRequest r) =>
        a.ApplicationName == r.ApplicationName && a.Description == r.Description && a.OwnerName == r.OwnerName &&
        a.OwnerEmail == r.OwnerEmail && a.TechnicalName == r.TechnicalName && a.TechnicalEmail == r.TechnicalEmail && a.Notes == r.Notes;
}
