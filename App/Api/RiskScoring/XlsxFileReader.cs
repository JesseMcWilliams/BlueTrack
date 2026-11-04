using System.Globalization;
using ClosedXML.Excel;

namespace BlueTrack.Api.RiskScoring;

/// <summary>
/// D-323: reads the first worksheet of an uploaded Excel workbook into the same rows CsvFileReader returns: the first
/// non-empty row is the header, each later non-empty row a dictionary from header to text. A date cell reads as
/// yyyy-MM-dd and a number as invariant text, as the CSV export writes them. ClosedXML, which already writes the
/// report exports, reads it.
/// </summary>
public static class XlsxFileReader
{
    public static IReadOnlyList<IReadOnlyDictionary<string, string>> ReadRows(Stream fileStream)
    {
        using var workbook = new XLWorkbook(fileStream);
        var sheet = workbook.Worksheets.First();
        var used = sheet.RangeUsed();
        if (used is null) return [];

        var rows = used.RowsUsed().ToList();
        var header = rows[0].Cells().Select(c => (Column: c.Address.ColumnNumber, Name: Text(c).Trim()))
            .Where(h => h.Name.Length > 0).ToList();
        if (rows.Count - 1 > CsvFileReader.MaxRows)
        {
            throw new InvalidOperationException($"The file has more than {CsvFileReader.MaxRows:N0} rows; split it and import each part.");
        }

        var result = new List<IReadOnlyDictionary<string, string>>(rows.Count - 1);
        foreach (var row in rows.Skip(1))
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (column, name) in header) values[name] = Text(row.Worksheet.Cell(row.RowNumber(), column));
            if (values.Values.Any(v => v.Length > 0)) result.Add(values);
        }
        return result;
    }

    private static string Text(IXLCell cell) => cell.DataType switch
    {
        XLDataType.DateTime => cell.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        XLDataType.Number => cell.GetDouble().ToString(CultureInfo.InvariantCulture),
        XLDataType.Boolean => cell.GetBoolean() ? "Yes" : "No",
        XLDataType.Blank => "",
        _ => cell.GetString()
    };
}
