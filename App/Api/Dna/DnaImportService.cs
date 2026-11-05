using BlueTrack.Api.Audit;
using BlueTrack.Api.Formatting;

namespace BlueTrack.Api.Dna;

/// <summary>A DNA workbook that can't be imported; the message is for the admin.</summary>
public sealed class DnaImportException(string message) : Exception(message);

/// <summary>What an import did.</summary>
public sealed record DnaImportOutcome(int AccountCount, string SheetCounts, int TrackedByRules, DnaImportSummary? Latest, int HardCodedCount = 0)
{
    public string Summary => $"{ServerTextCulture.Count(AccountCount)} accounts ({SheetCounts})"
        + (HardCodedCount > 0 ? $"; {ServerTextCulture.Count(HardCodedCount)} hard-coded credentials" : "")
        + (TrackedByRules > 0 ? $"; {ServerTextCulture.Count(TrackedByRules)} tracked by the DNA tracking rules" : "");
}

/// <summary>
/// D-325, D-341, D-342: one DNA import, from an upload or a data feed: read the workbook, replace the stored DNA
/// accounts, then let the enabled tracking rules track what they match, and audit it (the file name and counts, never a cell).
/// </summary>
public sealed class DnaImportService(DnaRepository repository, AuditLogger auditLogger, ILogger<DnaImportService> logger)
{
    public async Task<DnaImportOutcome> ImportAsync(Stream workbook, string fileName, int userKey, string how)
    {
        IReadOnlyList<DnaAccountRow> rows;
        IReadOnlyList<DnaHardCodedRow> hardCoded;
        try
        {
            (rows, hardCoded) = DnaReportReader.ReadWorkbook(workbook);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
        {
            throw new DnaImportException($"The workbook couldn't be read: {ex.Message}");
        }
        if (rows.Count == 0 && hardCoded.Count == 0)
            throw new DnaImportException("The workbook has no account sheets (sheets with an \"Account Name\" column), so nothing was changed.");

        var sheetCounts = string.Join("; ", rows.GroupBy(r => r.SheetCode).Select(g => $"{g.Key}={g.Count()}"));
        await repository.ReplaceAsync(fileName, userKey, rows, sheetCounts, hardCoded);
        await auditLogger.LogAsync("DnaImported", userKey, "dna_import", entityKey: null,
            detail: AuditLogger.ClipDetail($"DNA workbook '{fileName}' imported ({how}): {rows.Count} accounts ({sheetCounts}), {hardCoded.Count} hard-coded credentials; it replaces the previous DNA import."));

        var tracked = await TrackByRulesAsync(userKey);
        return new DnaImportOutcome(rows.Count, sheetCounts, tracked, await repository.GetLatestImportAsync(), hardCoded.Count);
    }

    /// <summary>D-342: tracks what each enabled rule matches, in rule order; never fails the import (a problem is logged).</summary>
    public async Task<int> TrackByRulesAsync(int userKey)
    {
        try
        {
            var matches = await repository.GetRuleMatchesAsync();
            var total = 0;
            foreach (var rule in matches.GroupBy(m => (m.RuleKey, m.RuleName)))
            {
                var linked = await repository.TrackAsync(rule.Select(m => m.DnaAccountKey).ToList(), $"Rule: {rule.Key.RuleName}");
                if (linked == 0) continue;
                total += linked;
                await auditLogger.LogAsync("DnaAccountsTracked", userKey, "dna_track_rule", entityKey: rule.Key.RuleKey.ToString(),
                    detail: $"The DNA tracking rule '{rule.Key.RuleName}' tracked {linked} DNA account(s) in BlueTrack.");
            }
            return total;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "The DNA tracking rules couldn't be applied after the import.");
            return 0;
        }
    }
}
