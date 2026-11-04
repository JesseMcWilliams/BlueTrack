using System.Globalization;
using ClosedXML.Excel;

namespace BlueTrack.Api.Dna;

/// <summary>One account from a DNA account-list workbook (D-325).</summary>
public sealed record DnaAccountRow(
    string SheetCode, bool IsDomainAccount, string? MachineName, string AccountName, string? DisplayName,
    string? AccountType, string? AccountCategory, string? AccountGroup, string? PrivilegedDomainGroup,
    string? InstanceName, string? DatabaseName, string? ComplianceStatus, string? AccountState,
    bool? PasswordNeverExpires, int? PasswordAgeDays, DateTime? PasswordLastSet, DateTime? LastLoginDate, DateTime? AccountExpirationDate);

/// <summary>
/// D-325: reads the account rows of a CyberArk Discovery &amp; Audit (DNA) account-list workbook. Each finding is a
/// sheet whose header row (one of its first rows) has an "Account Name" column; the Overview sheet has none. Never
/// read: the hard-coded credentials sheet (HCC), the raw database scan (DB-R, which the parsed DB-P-DB and DB-P-SRV
/// sheets repeat), and any column whose header names a credential or password length, so no secret is read even if
/// a report adds one elsewhere. Nothing is logged from the cells.
/// </summary>
public static class DnaReportReader
{
    private static readonly HashSet<string> SkippedSheets = new(StringComparer.OrdinalIgnoreCase) { "Overview", "HCC", "DB-R" };
    private const int HeaderSearchRows = 5;

    public static IReadOnlyList<DnaAccountRow> Read(Stream workbookStream)
    {
        using var workbook = new XLWorkbook(workbookStream);
        var rows = new List<DnaAccountRow>();
        foreach (var sheet in workbook.Worksheets.Where(s => !SkippedSheets.Contains(s.Name.Trim())))
        {
            var used = sheet.RangeUsed();
            if (used is null) continue;
            var header = FindHeader(sheet, used);
            if (header is null) continue;
            var (headerRow, columns) = header.Value;
            if (rows.Count + (used.LastRow().RowNumber() - headerRow) > RiskScoring.CsvFileReader.MaxRows)
            {
                throw new InvalidOperationException($"The workbook has more than {RiskScoring.CsvFileReader.MaxRows:N0} account rows.");
            }

            var code = sheet.Name.Trim();
            // Domain-account findings: their name doesn't belong to the machine they were found on.
            var isDomain = code.Contains("-DA", StringComparison.OrdinalIgnoreCase) || code.Equals("DS-SPN", StringComparison.OrdinalIgnoreCase);
            for (var r = headerRow + 1; r <= used.LastRow().RowNumber(); r++)
            {
                string? Text(string name) => columns.TryGetValue(name, out var c) ? Blank(CellText(sheet.Cell(r, c))) : null;
                var account = Text("Account Name");
                if (account is null) continue;
                rows.Add(new DnaAccountRow(
                    code, isDomain, Text("Machine Name"), account, Text("Account Display Name"),
                    Text("Account Type"), Text("Account Category"), Text("Account Group"), Text("Privileged Domain Group"),
                    Text("Instance Name"), Text("Database Name"), Text("Compliance Status"), Text("Account State"),
                    YesNo(Text("Password Never Expires")),
                    columns.TryGetValue("Password Age", out var ageColumn) ? Whole(sheet.Cell(r, ageColumn)) : null,
                    columns.TryGetValue("Password Last Set", out var setColumn) ? Date(sheet.Cell(r, setColumn)) : null,
                    columns.TryGetValue("Last Login Date", out var loginColumn) ? Date(sheet.Cell(r, loginColumn)) : null,
                    columns.TryGetValue("Account Expiration Date", out var expiryColumn) ? Date(sheet.Cell(r, expiryColumn)) : null));
            }
        }
        return rows;
    }

    /// <summary>The header row (one of the first few) and its columns by name, credential columns left out.</summary>
    private static (int Row, Dictionary<string, int> Columns)? FindHeader(IXLWorksheet sheet, IXLRange used)
    {
        var first = used.FirstRow().RowNumber();
        for (var r = first; r < first + HeaderSearchRows && r <= used.LastRow().RowNumber(); r++)
        {
            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var cell in sheet.Row(r).CellsUsed())
            {
                var name = CellText(cell).Trim();
                if (name.Length == 0 || IsSecret(name)) continue;
                columns.TryAdd(name, cell.Address.ColumnNumber);
            }
            if (columns.ContainsKey("Account Name")) return (r, columns);
        }
        return null;
    }

    private static bool IsSecret(string header) =>
        header.Contains("Credential", StringComparison.OrdinalIgnoreCase) || header.Contains("Password Length", StringComparison.OrdinalIgnoreCase);

    private static string CellText(IXLCell cell) => cell.DataType switch
    {
        XLDataType.Number => cell.GetDouble().ToString(CultureInfo.InvariantCulture),
        XLDataType.DateTime => cell.GetDateTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        XLDataType.Boolean => cell.GetBoolean() ? "Yes" : "No",
        XLDataType.Blank => "",
        _ => cell.GetString()
    };

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool? YesNo(string? value) => value?.ToLowerInvariant() switch
    {
        "yes" or "true" => true,
        "no" or "false" => false,
        _ => null
    };

    private static int? Whole(IXLCell cell) =>
        cell.DataType == XLDataType.Number ? (int)Math.Round(cell.GetDouble())
        : int.TryParse(cell.GetString().Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>A date cell, or a number Excel stores a date as; text such as "Never" or "N/A" is no date.</summary>
    private static DateTime? Date(IXLCell cell)
    {
        if (cell.DataType == XLDataType.DateTime) return cell.GetDateTime();
        if (cell.DataType == XLDataType.Number)
        {
            var serial = cell.GetDouble();
            return serial is > 0 and < 2958466 ? DateTime.FromOADate(serial) : null;
        }
        return DateTime.TryParse(cell.GetString().Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
    }
}
