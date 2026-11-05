using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Dna;
using BlueTrack.Api.Reports;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-325: CyberArk Discovery &amp; Audit (DNA) as a data source: upload the workbook on Admin > Data Sources
/// (ManageDataSources), and list its accounts on Reports > DNA Accounts (ViewDiscoveredAccounts, as the AD discovered
/// accounts are). D-341: sorting; a data feed can also import it. D-342: track DNA accounts in BlueTrack
/// (ManageDiscoveredAccounts, as Accept on discovered accounts), by hand or by tracking rules. D-343: purge and a
/// retention period. D-344: columns that need ViewDnaSensitiveColumns are blanked by the API for anyone else.
/// Audit entries record file names and counts, never a cell.
/// </summary>
[ApiController]
public sealed class DnaController(
    DnaRepository repository,
    DnaImportService importService,
    DnaTrackRuleRepository ruleRepository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger,
    ReportExportService exportService) : ControllerBase
{
    public const int MaxRuleConditions = 20;

    [HttpGet("api/admin/dna")]
    [Authorize(Policy = Permissions.ManageDataSources)]
    public async Task<IActionResult> GetLatest() => Ok(new
    {
        latest = await repository.GetLatestImportAsync(),
        settings = await repository.GetSettingsAsync(),
        restrictableColumns = DnaColumns.Restrictable
    });

    [HttpPost("api/admin/dna/import")]
    [Authorize(Policy = Permissions.ManageDataSources)]
    [RequestSizeLimit(30_000_000)]   // D-198: also outside IIS, whose default is ~30 MB
    public async Task<IActionResult> Import(IFormFile file)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        if (!file.FileName.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return Problem(title: "Can't import the file", detail: "Upload DNA's account-list workbook (.xlsx).", statusCode: StatusCodes.Status400BadRequest);
        }
        try
        {
            await using var stream = file.OpenReadStream();
            var outcome = await importService.ImportAsync(stream, Path.GetFileName(file.FileName), user.UserKey, "uploaded");
            return Ok(new { latest = outcome.Latest, trackedByRules = outcome.TrackedByRules });
        }
        catch (DnaImportException ex)
        {
            return Problem(title: "Can't import the file", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    /// <summary>D-343: deletes every DNA account and import now. Accounts already tracked in BlueTrack stay.</summary>
    [HttpPost("api/admin/dna/purge")]
    [Authorize(Policy = Permissions.ManageDataSources)]
    public async Task<IActionResult> Purge()
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        var deleted = await repository.PurgeAllAsync();
        await auditLogger.LogAsync("DnaPurged", user.UserKey, "dna_import", entityKey: null,
            detail: $"All DNA data was purged: {deleted} DNA account(s) and the import history. Accounts tracked in BlueTrack were kept.");
        return Ok(new { deleted });
    }

    public sealed class SaveDnaSettingsRequest
    {
        public int? RetentionDays { get; init; }
        public IReadOnlyList<string>? RestrictedColumns { get; init; }
    }

    /// <summary>D-343, D-344: the retention period (1-3650 days, or none) and the columns needing ViewDnaSensitiveColumns.</summary>
    [HttpPut("api/admin/dna/settings")]
    [Authorize(Policy = Permissions.ManageDataSources)]
    public async Task<IActionResult> SaveSettings([FromBody] SaveDnaSettingsRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        var errors = new List<string>();
        if (request.RetentionDays is < 1 or > 3650) errors.Add("Keep DNA data is 1 to 3,650 days, or blank to keep it until the next import or a purge.");
        var unknown = (request.RestrictedColumns ?? []).Where(c => !DnaColumns.Restrictable.Contains(c)).ToList();
        if (unknown.Count > 0) errors.Add($"Not a column that can be restricted: {string.Join(", ", unknown)}.");
        if (errors.Count > 0) return Problem(title: "Invalid DNA settings", detail: string.Join(" ", errors), statusCode: StatusCodes.Status400BadRequest);

        var before = await repository.GetSettingsAsync();
        var restricted = (request.RestrictedColumns ?? []).Distinct(StringComparer.Ordinal).ToList();
        await repository.SaveSettingsAsync(request.RetentionDays, restricted);
        var after = await repository.GetSettingsAsync();
        await auditLogger.LogAsync("DnaSettingsChanged", user.UserKey, "app_config", entityKey: null,
            detail: AuditLogger.ClipDetail($"Keep DNA data: {Days(before.RetentionDays)} -> {Days(after.RetentionDays)}. "
                + $"Columns needing ViewDnaSensitiveColumns: {List(before.RestrictedColumns)} -> {List(after.RestrictedColumns)}."));
        return Ok(after);

        static string Days(int? d) => d is null ? "until replaced" : $"{d} days";
        static string List(IReadOnlyList<string> c) => c.Count == 0 ? "none" : string.Join(", ", c);
    }

    // ---- D-342: tracking rules ----

    [HttpGet("api/admin/dna/track-rules")]
    [Authorize(Policy = Permissions.ManageDataSources)]
    public async Task<IActionResult> GetRules()
    {
        var rules = await ruleRepository.GetAllAsync();
        var matches = (await repository.GetRuleMatchesAsync()).GroupBy(m => m.RuleKey).ToDictionary(g => g.Key, g => g.Count());
        return Ok(new
        {
            fields = DnaTrackRuleFields.All,
            rules = rules.Select(r => new { r.DnaTrackRuleKey, r.RuleName, r.IsEnabled, r.Description, r.Conditions, matchingNow = matches.GetValueOrDefault(r.DnaTrackRuleKey) })
        });
    }

    [HttpPost("api/admin/dna/track-rules")]
    [Authorize(Policy = Permissions.ManageDataSources)]
    public Task<IActionResult> CreateRule([FromBody] SaveDnaTrackRuleRequest request) => SaveRuleAsync(null, request);

    [HttpPut("api/admin/dna/track-rules/{key:int}")]
    [Authorize(Policy = Permissions.ManageDataSources)]
    public Task<IActionResult> UpdateRule(int key, [FromBody] SaveDnaTrackRuleRequest request) => SaveRuleAsync(key, request);

    private async Task<IActionResult> SaveRuleAsync(int? key, SaveDnaTrackRuleRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(request.RuleName)) errors.Add("Name is required.");
        else if (request.RuleName.Trim().Length > 200) errors.Add("Name is at most 200 characters.");
        else if (await ruleRepository.NameTakenAsync(request.RuleName.Trim(), key)) errors.Add("Another DNA tracking rule has that name.");
        var conditions = request.Conditions ?? [];
        if (conditions.Count == 0) errors.Add("Add at least one condition: a rule without one would track every DNA account.");
        if (conditions.Count > MaxRuleConditions) errors.Add($"At most {MaxRuleConditions} conditions.");
        errors.AddRange(conditions.Select(DnaTrackRuleFields.Problem).OfType<string>());
        if (errors.Count > 0) return Problem(title: "Invalid DNA tracking rule", detail: string.Join(" ", errors), statusCode: StatusCodes.Status400BadRequest);

        var saved = await ruleRepository.SaveAsync(key, request, user.UserKey);
        if (saved is null) return NotFound();
        await auditLogger.LogAsync("DnaTrackRuleChanged", user.UserKey, "dna_track_rule", saved.Value.ToString(),
            detail: AuditLogger.ClipDetail($"DNA tracking rule '{request.RuleName!.Trim()}' {(key is null ? "created" : "saved")} ({(request.IsEnabled ? "on" : "off")}): "
                + string.Join("; ", conditions.Select(c => $"{c.FieldName} {c.Operator} {c.Value}".Trim()))));
        return key is null ? CreatedAtAction(nameof(GetRules), null, new { dnaTrackRuleKey = saved }) : Ok(new { dnaTrackRuleKey = saved });
    }

    [HttpDelete("api/admin/dna/track-rules/{key:int}")]
    [Authorize(Policy = Permissions.ManageDataSources)]
    public async Task<IActionResult> DeleteRule(int key)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        var name = (await ruleRepository.GetAllAsync()).FirstOrDefault(r => r.DnaTrackRuleKey == key)?.RuleName;
        if (name is null || !await ruleRepository.DeleteAsync(key)) return NotFound();
        await auditLogger.LogAsync("DnaTrackRuleChanged", user.UserKey, "dna_track_rule", key.ToString(), detail: $"DNA tracking rule '{name}' deleted.");
        return NoContent();
    }

    /// <summary>Applies the enabled rules now, to the DNA accounts already imported (they also run after each import).</summary>
    [HttpPost("api/admin/dna/track-rules/apply")]
    [Authorize(Policy = Permissions.ManageDataSources)]
    public async Task<IActionResult> ApplyRules()
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        return Ok(new { tracked = await importService.TrackByRulesAsync(user.UserKey) });
    }

    // ---- Reports > DNA Accounts ----

    private async Task<IReadOnlySet<string>> HiddenColumnsAsync() =>
        User.HasClaim(PermissionClaimsTransformation.PermissionClaimType, Permissions.ViewDnaSensitiveColumns)
            ? new HashSet<string>()
            : (await repository.GetSettingsAsync()).RestrictedColumns.ToHashSet(StringComparer.Ordinal);

    [HttpGet("api/reports/dna-accounts")]
    [Authorize(Policy = Permissions.ViewDiscoveredAccounts)]
    public async Task<IActionResult> GetAccounts([FromQuery] string? sheet = null, [FromQuery] bool? tracked = null, [FromQuery] string? search = null,
        [FromQuery] int? page = null, [FromQuery] int? pageSize = null, [FromQuery] string? sort = null, [FromQuery] bool? unresolvedSid = null)
    {
        var (normalizedPage, normalizedPageSize) = PagingParams.Normalize(page, pageSize);
        var hidden = await HiddenColumnsAsync();
        var (rows, matching, total) = await repository.GetPageAsync(sheet, tracked, search, normalizedPage, normalizedPageSize, sort, hidden, unresolvedSid);
        Response.Headers["X-Total-Count"] = total.ToString();
        Response.Headers["X-Filtered-Count"] = matching.ToString();
        Response.Headers["X-Hidden-Columns"] = string.Join(",", hidden.Order(StringComparer.Ordinal));   // D-344: the page says which are hidden
        return Ok(rows.Select(r => DnaColumns.Blank(r, hidden)));
    }

    [HttpGet("api/reports/dna-accounts/sheets")]
    [Authorize(Policy = Permissions.ViewDiscoveredAccounts)]
    public async Task<IActionResult> GetSheets() => Ok(await repository.GetSheetsAsync());

    // ---- D-345: Reports > DNA Hard-Coded Credentials (ViewDnaSensitiveColumns only) ----

    [HttpGet("api/reports/dna-hard-coded-credentials")]
    [Authorize(Policy = Permissions.ViewDnaSensitiveColumns)]
    public async Task<IActionResult> GetHardCoded([FromQuery] string? search = null, [FromQuery] string? sort = null,
        [FromQuery] int? page = null, [FromQuery] int? pageSize = null)
    {
        var (normalizedPage, normalizedPageSize) = PagingParams.Normalize(page, pageSize);
        var (rows, matching, total) = await repository.GetHardCodedAsync(search, sort, normalizedPage, normalizedPageSize);
        Response.Headers["X-Total-Count"] = total.ToString();
        Response.Headers["X-Filtered-Count"] = matching.ToString();
        return Ok(rows);
    }

    /// <summary>D-345: the whole list as the page filters and sorts it, as CSV or Excel; audit-logged as a ReportExport.</summary>
    [HttpGet("api/reports/dna-hard-coded-credentials/export")]
    [Authorize(Policy = Permissions.ViewDnaSensitiveColumns)]
    public async Task<IActionResult> ExportHardCoded([FromQuery] string? format, [FromQuery] string? search = null, [FromQuery] string? sort = null)
    {
        var (_, matching, _) = await repository.GetHardCodedAsync(search, sort, 1, 1);
        var parameters = new Dictionary<string, string?> { ["search"] = search, ["sort"] = sort };
        return await exportService.ExportAsync(this, ReportExports.DnaHardCodedCredentials, format, parameters, matching, async _ =>
        {
            var (rows, _, _) = await repository.GetHardCodedAsync(search, sort, 1, null);
            return ReportTable.From(ReportExports.DnaHardCodedCredentialsColumns, rows);
        });
    }

    public sealed class TrackDnaAccountsRequest
    {
        public IReadOnlyList<int>? DnaAccountKeys { get; init; }
    }

    /// <summary>D-342: tracks the chosen DNA accounts in BlueTrack (at most 1,000 at a time).</summary>
    [HttpPost("api/reports/dna-accounts/track")]
    [Authorize(Policy = Permissions.ManageDiscoveredAccounts)]
    public async Task<IActionResult> Track([FromBody] TrackDnaAccountsRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        var keys = (request.DnaAccountKeys ?? []).Distinct().ToList();
        if (keys.Count is 0 or > 1000)
            return Problem(title: "Can't track these accounts", detail: "Choose 1 to 1,000 DNA accounts.", statusCode: StatusCodes.Status400BadRequest);
        var linked = await repository.TrackAsync(keys, user.DisplayName ?? "an admin");
        await auditLogger.LogAsync("DnaAccountsTracked", user.UserKey, "dna_account", entityKey: null,
            detail: $"{linked} DNA account(s) tracked in BlueTrack ({keys.Count} chosen; the others were tracked already).");
        return Ok(new { tracked = linked });
    }
}
