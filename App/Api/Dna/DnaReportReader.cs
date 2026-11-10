using System.Globalization;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace BlueTrack.Api.Dna;

/// <summary>One account from a DNA account-list workbook (D-325; D-341 adds the newer format's columns as properties).</summary>
public sealed record DnaAccountRow(
    string SheetCode, bool IsDomainAccount, string? MachineName, string AccountName, string? DisplayName,
    string? AccountType, string? AccountCategory, string? AccountGroup, string? PrivilegedDomainGroup,
    string? InstanceName, string? DatabaseName, string? ComplianceStatus, string? AccountState,
    bool? PasswordNeverExpires, int? PasswordAgeDays, DateTime? PasswordLastSet, DateTime? LastLoginDate, DateTime? AccountExpirationDate)
{
    public string? MachineType { get; init; }
    public string? OsVersion { get; init; }
    public string? AccountDescription { get; init; }
    public string? ThreatCause { get; init; }
    public string? PassTheHashVulnerable { get; init; }
    public string? PassTheHashHashFound { get; init; }
    public string? ServiceAccountType { get; init; }
    public string? ServiceAccountDescription { get; init; }
    public string? SpnDescription { get; init; }
    public string? Roles { get; init; }
    public string? DbPermissions { get; init; }
    public string? Details { get; init; }
    public string? LocalMappedAccount { get; init; }
    /// <summary>D-347: DNA couldn't resolve the account; its name (and the domain in Account Type) is its SID.</summary>
    public bool IsUnresolvedSid { get; init; }
}

/// <summary>
/// D-345: one row of DNA's hard-coded credentials sheet. CredentialLocation is DNA's "Hard-Coded Credential" text: the
/// file's line and its format with the password masked (asterisks), masked again by <see cref="DnaReportReader.MaskPasswords"/>.
/// </summary>
public sealed record DnaHardCodedRow(
    string SheetCode, string? MachineName, string? MachineType, string? ApplicationServer, string? ApplicationName, string? SiteName,
    string? AccountName, string? HardCodedInFile, string? CredentialLocation, int? PasswordLength, string? TargetSystemAddress,
    string? TargetSystemType, string? OsVersion, string? Details);

/// <summary>What a DNA workbook holds: its accounts, and (D-345) its hard-coded credentials.</summary>
public sealed record DnaWorkbook(IReadOnlyList<DnaAccountRow> Accounts, IReadOnlyList<DnaHardCodedRow> HardCoded);

/// <summary>
/// D-325: reads the account rows of a CyberArk Discovery &amp; Audit (DNA) workbook. Each account sheet has a header row
/// with an "Account Name" column; sheets without one (an overview, an executive summary, explanations) are passed over.
/// Two formats: the account-list report (a sheet per finding: NC-DA, NAC-LA-D, DS-SPN, DB-P-SRV, ...) and, D-341, the
/// newer report (Windows Scan, Unix Scan, Domain Scan, Database Scan), whose header sits under a summary block, so up to
/// <see cref="HeaderSearchRows"/> rows are searched. Never read: a sheet of hard-coded credentials, whatever it is called
/// (HCC, Hard-Coded Credentials: any sheet so named, or whose header has "Hard-Coded in File"), the raw database scan (DB-R, which
/// the parsed DB-P-DB and DB-P-SRV sheets repeat), and any column whose header names a credential or password length,
/// so no secret is read even if a report adds one elsewhere. Nothing is logged from the cells.
/// D-345 (the user's choice of 2026-11-05): the hard-coded credentials sheet is read too, into its own rows, with DNA's
/// masked credential text (masked again here) and nothing else of a secret.
/// D-353: the workbook is read forward only, a row at a time (the OpenXML SDK's reader), not loaded whole: only the
/// shared strings, the date styles and the rows read so far are held.
/// </summary>
public static class DnaReportReader
{
    private static readonly HashSet<string> SkippedSheets = new(StringComparer.OrdinalIgnoreCase) { "Overview", "DB-R" };
    internal const int HeaderSearchRows = 30;

    /// <summary>The accounts only (the import's account sheets).</summary>
    public static IReadOnlyList<DnaAccountRow> Read(Stream workbookStream) => ReadWorkbook(workbookStream).Accounts;

    public static DnaWorkbook ReadWorkbook(Stream workbookStream)
    {
        using var document = SpreadsheetDocument.Open(workbookStream, false);
        var workbookPart = document.WorkbookPart ?? throw new InvalidDataException("The file has no workbook.");
        var strings = ReadSharedStrings(workbookPart);
        var dateStyles = ReadDateStyles(workbookPart);
        var rows = new List<DnaAccountRow>();
        var hardCodedRows = new List<DnaHardCodedRow>();
        foreach (var sheet in workbookPart.Workbook?.Sheets?.Elements<Sheet>() ?? [])
        {
            var name = (sheet.Name?.Value ?? "").Trim();
            if (SkippedSheets.Contains(name) || sheet.Id?.Value is not { } id) continue;
            if (workbookPart.GetPartById(id) is not WorksheetPart part) continue;   // a chart sheet
            ReadSheet(part, name, strings, dateStyles, rows, hardCodedRows);
        }
        return new DnaWorkbook(rows, hardCodedRows);
    }

    /// <summary>One sheet, a row at a time: finds the header, then reads the rows as accounts or hard-coded credentials.</summary>
    private static void ReadSheet(WorksheetPart part, string code, List<string> strings, HashSet<uint> dateStyles,
        List<DnaAccountRow> rows, List<DnaHardCodedRow> hardCodedRows)
    {
        int? firstUsed = null;
        Dictionary<string, int>? columns = null;    // the account sheets' columns, credential columns left out
        Dictionary<string, int>? allColumns = null; // every column, for a hard-coded credentials sheet
        var hardCodedSheet = false;
        var headerRow = 0;
        var rowsBefore = rows.Count;
        // Account Type says it in both formats ("Domain: x", "Local", "Windows Domain User: x", ...); a sheet without that
        // column falls back to the account-list report's domain findings (-DA, DS-SPN) and the Domain Scan.
        var domainSheet = code.Contains("-DA", StringComparison.OrdinalIgnoreCase) || code.Equals("DS-SPN", StringComparison.OrdinalIgnoreCase)
            || code.Equals("Domain Scan", StringComparison.OrdinalIgnoreCase);

        using var reader = OpenXmlReader.Create(part);
        var lastRow = 0;
        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement) continue;
            var row = (Row)reader.LoadCurrentElement()!;
            var r = row.RowIndex?.Value is { } index ? (int)index : lastRow + 1;
            lastRow = r;
            var cells = ReadCells(row, strings, dateStyles);
            if (cells.Count == 0) continue;
            firstUsed ??= r;

            if (columns is null)
            {
                if (r >= firstUsed + HeaderSearchRows) return;   // no header near the top: not an account sheet
                var (found, all, hardCoded) = HeaderColumns(cells);
                if (!found.ContainsKey("Account Name")) continue;
                (columns, allColumns, headerRow) = (found, all, r);
                hardCodedSheet = hardCoded || code.Equals("HCC", StringComparison.OrdinalIgnoreCase)
                    || code.Contains("Hard-Coded", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (hardCodedSheet)
            {
                if (ReadHardCodedRow(code, cells, allColumns!) is { } hardCodedRow) hardCodedRows.Add(hardCodedRow);
                continue;
            }
            if (rowsBefore + (r - headerRow) > RiskScoring.CsvFileReader.MaxRows)
            {
                throw new InvalidOperationException($"The workbook has more than {RiskScoring.CsvFileReader.MaxRows:N0} account rows.");
            }
            if (ReadAccountRow(code, domainSheet, cells, columns) is { } account) rows.Add(account);
        }
    }

    private static DnaAccountRow? ReadAccountRow(string code, bool domainSheet, Dictionary<int, XlValue> cells, Dictionary<string, int> columns)
    {
        XlValue Cell(string name) => columns.TryGetValue(name, out var c) && cells.TryGetValue(c, out var v) ? v : XlValue.Blank;
        string? Text(string name) => Blank(Cell(name).Text);
        string? Starting(string prefix) => columns.Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(kv => Blank(cells.TryGetValue(kv.Value, out var v) ? v.Text : "")).FirstOrDefault(v => v is not null);
        var account = Text("Account Name");
        if (account is null) return null;
        var accountType = Text("Account Type");
        var isDomain = accountType is null ? domainSheet : IsDomainType(accountType);
        return new DnaAccountRow(
            code, isDomain, Text("Machine Name"), account, Text("Account Display Name"),
            accountType, Text("Account Category"), Text("Account Group"), Text("Privileged Domain Group"),
            Text("Instance Name"), Text("Database Name"), Text("Compliance Status"), Text("Account State"),
            YesNo(Text("Password Never Expires")),
            Cell("Password Age").Whole, Cell("Password Last Set").Date, Cell("Last Login Date").Date, Cell("Account Expiration Date").Date)
        {
            MachineType = Text("Machine Type"),
            OsVersion = Text("OS Version"),
            AccountDescription = Text("Account Description"),
            ThreatCause = Text("Threat Cause"),
            PassTheHashVulnerable = Text("Pass-the-Hash: Vulnerable"),
            PassTheHashHashFound = Text("Pass-the-Hash: Hash Found"),
            ServiceAccountType = Text("Service Account Type"),
            ServiceAccountDescription = Text("Service Account Description"),
            SpnDescription = Text("SPN Description"),
            Roles = Text("Roles"),
            DbPermissions = Text("Permissions"),
            Details = Text("Details") ?? Starting("Insecure Privilege Escalation"),
            LocalMappedAccount = Text("Local Mapped Account"),
            IsUnresolvedSid = IsSid(account) || (accountType is not null && IsSid(DomainOf(accountType)))
        };
    }

    /// <summary>
    /// D-345: a row of the hard-coded credentials sheet, whose header is taken with every column (the account sheets drop
    /// any credential column); "Hard-Coded Credential" is kept only as DNA's masked text, masked again.
    /// </summary>
    private static DnaHardCodedRow? ReadHardCodedRow(string code, Dictionary<int, XlValue> cells, Dictionary<string, int> columns)
    {
        XlValue Cell(string name) => columns.TryGetValue(name, out var c) && cells.TryGetValue(c, out var v) ? v : XlValue.Blank;
        string? Text(string name) => Blank(Cell(name).Text);
        var file = Text("Hard-Coded in File");
        var account = Text("Account Name");
        var machine = Text("Machine Name");
        if (file is null && account is null && machine is null) return null;
        return new DnaHardCodedRow(
            code, machine, Text("Machine Type"), Text("Application Server"), Text("Application Name"), Text("Site Name"),
            account, file, MaskPasswords(Text("Hard-Coded Credential")), Cell("Password Length").Whole,
            Text("Target System Address"), Text("Target System Type"), Text("OS Version"), Text("Details"));
    }

    private static readonly System.Text.RegularExpressions.Regex PasswordValue = new(
        @"(?<key>\b(?:password|passwd|pwd|pass|secret|client_secret|api[_-]?key)\s*[=:]\s*)(?<quote>[""']?)(?<value>[^;""'\s,}]+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// D-345: DNA shows a hard-coded password as asterisks; any password-style value (password=, pwd=, secret=, ...) that
    /// is still readable is replaced by asterisks of the same length, so no password is ever stored.
    /// </summary>
    public static string? MaskPasswords(string? text) => text is null ? null
        : PasswordValue.Replace(text, m => m.Groups["value"].Value.All(ch => ch == '*')
            ? m.Value
            : m.Groups["key"].Value + m.Groups["quote"].Value + new string('*', m.Groups["value"].Value.Length));

    /// <summary>
    /// A candidate header row's columns by name: Found leaves the credential columns out (the account sheets), All keeps
    /// them (a hard-coded credentials sheet); HardCoded when the row has "Hard-Coded in File".
    /// </summary>
    private static (Dictionary<string, int> Found, Dictionary<string, int> All, bool HardCoded) HeaderColumns(Dictionary<int, XlValue> cells)
    {
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var all = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var hardCoded = false;
        foreach (var (column, value) in cells.OrderBy(c => c.Key))
        {
            var name = value.Text.Trim();
            if (name.Length == 0) continue;
            all.TryAdd(name, column);
            if (name.Equals("Hard-Coded in File", StringComparison.OrdinalIgnoreCase)) hardCoded = true;
            if (IsSecret(name)) continue;
            found.TryAdd(name, column);
        }
        return (found, all, hardCoded);
    }

    /// <summary>D-347: a Windows security identifier (S-1-5-21-...), which DNA gives for an account it couldn't resolve.</summary>
    internal static bool IsSid(string? value) =>
        value is not null && System.Text.RegularExpressions.Regex.IsMatch(value.Trim(), @"^S-1-\d+(-\d+)+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string? DomainOf(string accountType) => accountType.Contains(':') ? accountType[(accountType.IndexOf(':') + 1)..].Trim() : null;

    /// <summary>"Domain: x", "Windows Domain User: x", "Windows Domain Group: x": a domain account (or group).</summary>
    internal static bool IsDomainType(string accountType) =>
        accountType.StartsWith("Domain", StringComparison.OrdinalIgnoreCase)
        || accountType.StartsWith("Windows Domain", StringComparison.OrdinalIgnoreCase);

    private static bool IsSecret(string header) =>
        header.Contains("Credential", StringComparison.OrdinalIgnoreCase) || header.Contains("Password Length", StringComparison.OrdinalIgnoreCase);

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool? YesNo(string? value) => value?.ToLowerInvariant() switch
    {
        "yes" or "true" => true,
        "no" or "false" => false,
        _ => null
    };

    // ---- Reading the workbook's XML (D-353) ----

    /// <summary>The shared strings, in order; an item's text is its runs' text (phonetic hints left out, as Excel shows it).</summary>
    private static List<string> ReadSharedStrings(WorkbookPart workbookPart)
    {
        var strings = new List<string>();
        if (workbookPart.SharedStringTablePart is not { } part) return strings;
        using var reader = OpenXmlReader.Create(part);
        while (reader.Read())
        {
            if (reader.ElementType != typeof(SharedStringItem) || !reader.IsStartElement) continue;
            strings.Add(ItemText((OpenXmlCompositeElement)reader.LoadCurrentElement()!));
        }
        return strings;
    }

    private static string ItemText(OpenXmlCompositeElement item) =>
        item.GetFirstChild<Text>() is { } text ? text.Text : string.Concat(item.Elements<Run>().Select(run => run.Text?.Text));

    /// <summary>The cell styles (indexes into cellXfs) whose number format shows a date.</summary>
    private static HashSet<uint> ReadDateStyles(WorkbookPart workbookPart)
    {
        var dates = new HashSet<uint>();
        var stylesheet = workbookPart.WorkbookStylesPart?.Stylesheet;
        if (stylesheet?.CellFormats is null) return dates;
        var custom = new Dictionary<uint, string>();
        foreach (var format in stylesheet.NumberingFormats?.Elements<NumberingFormat>() ?? [])
        {
            if (format.NumberFormatId?.Value is { } formatId) custom[formatId] = format.FormatCode?.Value ?? "";
        }
        uint index = 0;
        foreach (var format in stylesheet.CellFormats.Elements<CellFormat>())
        {
            var id = format.NumberFormatId?.Value ?? 0;
            if (custom.TryGetValue(id, out var code) ? IsDateFormatCode(code) : IsBuiltInDate(id)) dates.Add(index);
            index++;
        }
        return dates;
    }

    /// <summary>Excel's built-in date and date-time formats; the time-only ones (18-21, 45-47) stay numbers.</summary>
    private static bool IsBuiltInDate(uint id) => id is (>= 14 and <= 17) or 22 or (>= 27 and <= 36) or (>= 50 and <= 58);

    /// <summary>A custom number format with a day, month or year part outside quotes and brackets ("0.00" and "[Red]0" aren't).</summary>
    internal static bool IsDateFormatCode(string code)
    {
        var inQuote = false;
        var inBracket = false;
        for (var i = 0; i < code.Length; i++)
        {
            var ch = code[i];
            if (inQuote) { if (ch == '"') inQuote = false; continue; }
            if (inBracket) { if (ch == ']') inBracket = false; continue; }
            switch (ch)
            {
                case '"': inQuote = true; continue;
                case '[': inBracket = true; continue;
                case '\\' or '_' or '*': i++; continue;   // an escaped, padding or fill character
                case 'y' or 'Y' or 'd' or 'D' or 'e' or 'E': return true;
                case 'm' or 'M':
                    // "m" right after an hour or before seconds is minutes.
                    var before = code[..i].TrimEnd(':', ' ', '.', 'm', 'M').LastOrDefault();
                    var after = code[(i + 1)..].TrimStart('m', 'M', ':', ' ', '.').FirstOrDefault();
                    if (before is 'h' or 'H' || after is 's' or 'S') continue;
                    return true;
            }
        }
        return false;
    }

    /// <summary>A row's non-blank cells by column number.</summary>
    private static Dictionary<int, XlValue> ReadCells(Row row, List<string> strings, HashSet<uint> dateStyles)
    {
        var cells = new Dictionary<int, XlValue>();
        var column = 0;
        foreach (var cell in row.Elements<Cell>())
        {
            column = cell.CellReference?.Value is { } reference ? ColumnNumber(reference) : column + 1;
            var value = ReadValue(cell, strings, dateStyles);
            if (value.Kind != XlKind.Blank) cells[column] = value;
        }
        return cells;
    }

    private static XlValue ReadValue(Cell cell, List<string> strings, HashSet<uint> dateStyles)
    {
        var type = cell.DataType?.Value;
        if (type == CellValues.InlineString) return cell.InlineString is { } inline ? XlValue.FromText(ItemText(inline)) : XlValue.Blank;
        var raw = cell.CellValue?.Text;
        if (raw is null) return XlValue.Blank;
        if (type == CellValues.SharedString)
        {
            return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < strings.Count
                ? XlValue.FromText(strings[i]) : XlValue.Blank;
        }
        if (type == CellValues.Boolean) return new XlValue(XlKind.Boolean, raw.Trim() is "1" or "true" ? 1 : 0, null);
        if (type == CellValues.Date)
        {
            return DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? new XlValue(XlKind.DateTime, d.ToOADate(), null) : XlValue.FromText(raw);
        }
        if (type == CellValues.String || type == CellValues.Error) return XlValue.FromText(raw);
        if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) return XlValue.FromText(raw);
        var isDate = cell.StyleIndex?.Value is { } style && dateStyles.Contains(style) && number is > -657435 and < 2958466;
        return new XlValue(isDate ? XlKind.DateTime : XlKind.Number, number, null);
    }

    /// <summary>"AB12" is column 28.</summary>
    private static int ColumnNumber(string reference)
    {
        var n = 0;
        foreach (var ch in reference)
        {
            if (!char.IsAsciiLetter(ch)) break;
            n = n * 26 + (char.ToUpperInvariant(ch) - 'A' + 1);
        }
        return n;
    }

    private enum XlKind { Blank, Text, Number, DateTime, Boolean }

    /// <summary>A cell's value as the reader needs it: its text, a whole number, or a date.</summary>
    private readonly record struct XlValue(XlKind Kind, double Number, string? String)
    {
        public static readonly XlValue Blank = new(XlKind.Blank, 0, null);
        public static XlValue FromText(string text) => text.Length == 0 ? Blank : new(XlKind.Text, 0, text);

        public string Text => Kind switch
        {
            XlKind.Number => Number.ToString(CultureInfo.InvariantCulture),
            XlKind.DateTime => DateTime.FromOADate(Number).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            XlKind.Boolean => Number != 0 ? "Yes" : "No",
            XlKind.Text => String!,
            _ => ""
        };

        public int? Whole => Kind switch
        {
            XlKind.Number => (int)Math.Round(Number),
            XlKind.Text => int.TryParse(String!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null,
            _ => null
        };

        /// <summary>A date cell, or a number Excel stores a date as; text such as "Never" or "N/A" is no date.</summary>
        public DateTime? Date => Kind switch
        {
            XlKind.DateTime => DateTime.FromOADate(Number),
            XlKind.Number => Number is > 0 and < 2958466 ? DateTime.FromOADate(Number) : null,
            XlKind.Text => DateTime.TryParse(String!.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
            _ => null
        };
    }
}
