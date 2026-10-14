using System.Globalization;
using System.Text;

namespace BlueTrack.Api.Reports;

/// <summary>
/// D-220: writes a <see cref="ReportTable"/> as CSV -- UTF-8 with a BOM (so
/// Excel reads non-ASCII text correctly), RFC 4180 (comma separated, CRLF
/// line ends, a field holding a comma, quote, CR or LF is quoted with its
/// quotes doubled). Written by hand rather than through CsvHelper so the
/// formula-injection guard is explicit: a text cell starting with =, +, -,
/// @, tab or CR gets a leading apostrophe, so a spreadsheet opening the file
/// shows it as text instead of running it. Numbers and dates are written by
/// this class, never from user text, so they aren't guarded.
/// </summary>
public static class CsvReportWriter
{
    public const string ContentType = "text/csv; charset=utf-8";

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public static byte[] Write(ReportTable table)
    {
        var text = new StringBuilder();
        AppendRow(text, table.Headers);
        foreach (var row in table.Rows) AppendRow(text, row);

        var preamble = Utf8WithBom.GetPreamble();
        var body = Utf8WithBom.GetBytes(text.ToString());
        var bytes = new byte[preamble.Length + body.Length];
        preamble.CopyTo(bytes, 0);
        body.CopyTo(bytes, preamble.Length);
        return bytes;
    }

    private static void AppendRow(StringBuilder text, IEnumerable<object?> cells)
    {
        var first = true;
        foreach (var cell in cells)
        {
            if (!first) text.Append(',');
            text.Append(Field(cell));
            first = false;
        }
        text.Append("\r\n");
    }

    /// <summary>One cell as CSV field text: formatted, guarded if it's text, quoted if it needs to be.</summary>
    public static string Field(object? value)
    {
        var text = value switch
        {
            null => "",
            string s => GuardFormula(s),
            DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => GuardFormula(value.ToString() ?? "")
        };
        return text.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? "\"" + text.Replace("\"", "\"\"") + "\""
            : text;
    }

    /// <summary>Prefixes an apostrophe to text a spreadsheet would treat as a formula (OWASP CSV injection).</summary>
    public static string GuardFormula(string text) =>
        text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + text : text;
}
