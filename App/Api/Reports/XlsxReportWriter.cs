using ClosedXML.Excel;

namespace BlueTrack.Api.Reports;

/// <summary>
/// D-220: writes a <see cref="ReportTable"/> as an Excel workbook (ClosedXML)
/// with one sheet: a bold, frozen header row with an auto-filter. Text is
/// always stored as a text value, never as a formula, so a cell starting
/// with "=" can't run; numbers are numeric cells and dates are date cells
/// (shown yyyy-mm-dd, as on the pages). Column widths come from the text
/// length of the header and the first rows, not from font measurement, so
/// the server needs no fonts and a large export stays quick.
/// </summary>
public static class XlsxReportWriter
{
    public const string ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Excel's limit on the characters in one cell.</summary>
    public const int MaxCellText = 32767;

    private const int WidthSampleRows = 200;

    public static byte[] Write(ReportTable table, string sheetName)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(SheetName(sheetName));
        var columnCount = table.Headers.Count;
        var widths = new int[columnCount];
        var isDateColumn = new bool[columnCount];

        for (var c = 0; c < columnCount; c++)
        {
            sheet.Cell(1, c + 1).Value = table.Headers[c];
            widths[c] = table.Headers[c].Length;
        }

        for (var r = 0; r < table.Rows.Count; r++)
        {
            var row = table.Rows[r];
            for (var c = 0; c < columnCount; c++)
            {
                var value = row[c];
                if (value is null) continue;
                var cell = sheet.Cell(r + 2, c + 1);
                switch (value)
                {
                    case string s:
                        cell.Value = s.Length > MaxCellText ? s[..MaxCellText] : s;
                        break;
                    case DateOnly d:
                        cell.Value = d.ToDateTime(TimeOnly.MinValue);
                        isDateColumn[c] = true;
                        break;
                    case DateTime dt:
                        cell.Value = dt;
                        isDateColumn[c] = true;
                        break;
                    case int or long or short or byte or decimal or double or float:
                        cell.Value = Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture);
                        break;
                    default:
                        cell.Value = value.ToString() ?? "";
                        break;
                }
                if (r < WidthSampleRows)
                    widths[c] = Math.Max(widths[c], value is DateOnly or DateTime ? 10 : (value.ToString() ?? "").Length);
            }
        }

        var lastRow = table.Rows.Count + 1;
        var header = sheet.Range(1, 1, 1, columnCount);
        header.Style.Font.Bold = true;
        sheet.SheetView.FreezeRows(1);
        sheet.Range(1, 1, lastRow, columnCount).SetAutoFilter();
        for (var c = 0; c < columnCount; c++)
        {
            if (isDateColumn[c] && lastRow > 1) sheet.Range(2, c + 1, lastRow, c + 1).Style.NumberFormat.Format = "yyyy-mm-dd";
            // +3 leaves room for the auto-filter's drop-down button.
            sheet.Column(c + 1).Width = Math.Clamp(widths[c] + 3, 8, 60);
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    /// <summary>Excel sheet names: at most 31 characters, none of : \ / ? * [ ].</summary>
    public static string SheetName(string name)
    {
        var cleaned = new string(name.Select(ch => ch is ':' or '\\' or '/' or '?' or '*' or '[' or ']' ? '-' : ch).ToArray()).Trim();
        if (cleaned.Length == 0) cleaned = "Report";
        return cleaned.Length > 31 ? cleaned[..31] : cleaned;
    }
}
