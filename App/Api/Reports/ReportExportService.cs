using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;

namespace BlueTrack.Api.Reports;

/// <summary>
/// D-220: the shared part of every report's export endpoint. Each endpoint
/// sits beside its report's own GET, under the same [Authorize] policy, and
/// passes how many rows match the report's current filters, plus a loader.
/// This checks the format, refuses (400, with the count and the limit) when
/// more rows match than web.app_config.ExportMaxRows -- never a silently cut
/// short file -- writes the CSV or .xlsx, audit-logs a ReportExport event
/// (who, report, format, parameters, rows) and returns the file as
/// BlueTrack_&lt;Report&gt;_&lt;yyyyMMdd-HHmm&gt; (server local time).
/// </summary>
public sealed class ReportExportService(
    AppConfigRepository appConfigRepository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger)
{
    public const string AuditEventType = "ReportExport";

    /// <param name="matchingCount">Rows the report's current filters match (every page).</param>
    /// <param name="buildTable">Loads the rows and builds the table; its argument is the most rows to load (the limit plus one, so a row added since the count still trips the limit).</param>
    public async Task<IActionResult> ExportAsync(
        ControllerBase controller,
        ReportExportInfo report,
        string? format,
        IReadOnlyDictionary<string, string?> parameters,
        int matchingCount,
        Func<int, Task<ReportTable>> buildTable)
    {
        var isCsv = string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
        if (!isCsv && !string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase))
        {
            return controller.Problem(title: "Invalid export format", detail: "format must be csv or xlsx.", statusCode: StatusCodes.Status400BadRequest);
        }

        var user = await currentUserResolver.ResolveAsync(controller.User);
        if (user is null) return controller.Unauthorized();

        var maxRows = (await appConfigRepository.GetAsync()).ExportMaxRows;
        if (matchingCount > maxRows) return TooManyRows(controller, matchingCount, maxRows);

        var table = await buildTable(maxRows + 1);
        if (table.Rows.Count > maxRows) return TooManyRows(controller, table.Rows.Count, maxRows);

        var bytes = isCsv ? CsvReportWriter.Write(table) : XlsxReportWriter.Write(table, report.Title);
        var formatName = isCsv ? "CSV" : "Excel";

        var sent = parameters.Where(p => !string.IsNullOrEmpty(p.Value)).Select(p => $"{p.Key}={p.Value}").ToList();
        await auditLogger.LogAsync(AuditEventType, user.UserKey, "report", report.Key,
            detail: AuditLogger.ClipDetail(
                $"{report.Title} exported as {formatName}, {table.Rows.Count} rows; "
                + (sent.Count == 0 ? "no filters or sort" : string.Join(", ", sent))));

        var fileName = $"BlueTrack_{report.FileStem}_{DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture)}.{(isCsv ? "csv" : "xlsx")}";
        return controller.File(bytes, isCsv ? CsvReportWriter.ContentType : XlsxReportWriter.ContentType, fileName);
    }

    /// <summary>For a report the API returns whole (no paging): the rows are already loaded, so the count is theirs.</summary>
    public Task<IActionResult> ExportAsync<T>(
        ControllerBase controller,
        ReportExportInfo report,
        string? format,
        IReadOnlyDictionary<string, string?> parameters,
        IReadOnlyList<T> rows,
        IReadOnlyList<ExportColumn<T>> columns) =>
        ExportAsync(controller, report, format, parameters, rows.Count, _ => Task.FromResult(ReportTable.From(columns, rows)));

    private static ObjectResult TooManyRows(ControllerBase controller, int matching, int maxRows) => controller.Problem(
        title: "Too many rows to export",
        detail: $"{matching.ToString("N0", CultureInfo.InvariantCulture)} rows match this report, more than the export limit of "
            + $"{maxRows.ToString("N0", CultureInfo.InvariantCulture)} rows. Narrow the report's filters and export again, "
            + "or ask an administrator to raise Export Max Rows on Global Application Configuration.",
        statusCode: StatusCodes.Status400BadRequest);

    public static IReadOnlyDictionary<string, string?> NoParameters { get; } = new Dictionary<string, string?>();
}
