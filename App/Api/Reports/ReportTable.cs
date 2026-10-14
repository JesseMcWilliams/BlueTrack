namespace BlueTrack.Api.Reports;

/// <summary>D-220: one exported column -- the header the report's grid shows, and how to read the cell from a row.</summary>
public sealed record ExportColumn<T>(string Header, Func<T, object?> Value);

/// <summary>
/// D-220: what an export writes, independent of the format: the header row
/// and the data rows. A cell is null (blank), a string (always written as
/// text), a number (int, long, decimal, double), a DateOnly, or (D-231) a
/// DateTime that keeps its time; the writers type each one. Bools are
/// spelled out ("Yes"/"No") by the column itself, as the grid shows them.
/// </summary>
public sealed class ReportTable
{
    public ReportTable(IReadOnlyList<string> headers, IReadOnlyList<object?[]> rows)
    {
        if (rows.Any(r => r.Length != headers.Count))
            throw new ArgumentException("Every row needs one cell per header.", nameof(rows));
        Headers = headers;
        Rows = rows;
    }

    public IReadOnlyList<string> Headers { get; }
    public IReadOnlyList<object?[]> Rows { get; }

    public static ReportTable From<T>(IReadOnlyList<ExportColumn<T>> columns, IEnumerable<T> rows) =>
        new(columns.Select(c => c.Header).ToList(),
            rows.Select(row => columns.Select(c => c.Value(row)).ToArray()).ToList());
}
