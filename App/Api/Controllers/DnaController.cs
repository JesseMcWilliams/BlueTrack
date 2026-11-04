using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Dna;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-325: CyberArk Discovery &amp; Audit (DNA) as a data source: upload the account-list workbook on Admin > Data
/// Sources (ManageDataSources), and list its accounts on Reports > DNA Accounts (ViewDiscoveredAccounts, as the AD
/// discovered accounts are). The audit entry records the file name and counts, never a cell.
/// </summary>
[ApiController]
public sealed class DnaController(DnaRepository repository, CurrentUserResolver currentUserResolver, AuditLogger auditLogger) : ControllerBase
{
    [HttpGet("api/admin/dna")]
    [Authorize(Policy = Permissions.ManageDataSources)]
    public async Task<IActionResult> GetLatest() => Ok(new { latest = await repository.GetLatestImportAsync() });

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

        IReadOnlyList<DnaAccountRow> rows;
        try
        {
            rows = DnaReportReader.Read(file.OpenReadStream());
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
        {
            return Problem(title: "Can't import the file", detail: $"The workbook couldn't be read: {ex.Message}", statusCode: StatusCodes.Status400BadRequest);
        }
        if (rows.Count == 0)
        {
            return Problem(title: "Can't import the file", detail: "The workbook has no account sheets (sheets with an \"Account Name\" column), so nothing was changed.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        var sheetCounts = string.Join("; ", rows.GroupBy(r => r.SheetCode).Select(g => $"{g.Key}={g.Count()}"));
        var fileName = Path.GetFileName(file.FileName);
        await repository.ReplaceAsync(fileName, user.UserKey, rows, sheetCounts);
        await auditLogger.LogAsync("DnaImported", user.UserKey, "dna_import", entityKey: null,
            detail: AuditLogger.ClipDetail($"DNA workbook '{fileName}' imported: {rows.Count} accounts ({sheetCounts}); it replaces the previous DNA import."));
        return Ok(new { latest = await repository.GetLatestImportAsync() });
    }

    [HttpGet("api/reports/dna-accounts")]
    [Authorize(Policy = Permissions.ViewDiscoveredAccounts)]
    public async Task<IActionResult> GetAccounts([FromQuery] string? sheet = null, [FromQuery] bool? tracked = null, [FromQuery] string? search = null,
        [FromQuery] int? page = null, [FromQuery] int? pageSize = null)
    {
        var (normalizedPage, normalizedPageSize) = PagingParams.Normalize(page, pageSize);
        var (rows, matching, total) = await repository.GetPageAsync(sheet, tracked, search, normalizedPage, normalizedPageSize);
        Response.Headers["X-Total-Count"] = total.ToString();
        Response.Headers["X-Filtered-Count"] = matching.ToString();
        return Ok(rows);
    }

    [HttpGet("api/reports/dna-accounts/sheets")]
    [Authorize(Policy = Permissions.ViewDiscoveredAccounts)]
    public async Task<IActionResult> GetSheets() => Ok(await repository.GetSheetsAsync());
}
