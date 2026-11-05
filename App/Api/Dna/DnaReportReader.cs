using System.Globalization;
using ClosedXML.Excel;

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
/// </summary>
public static class DnaReportReader
{
    private static readonly HashSet<string> SkippedSheets = new(StringComparer.OrdinalIgnoreCase) { "Overview", "DB-R" };
    internal const int HeaderSearchRows = 30;

    /// <summary>The accounts only (the import's account sheets).</summary>
    public static IReadOnlyList<DnaAccountRow> Read(Stream workbookStream) => ReadWorkbook(workbookStream).Accounts;

    public static DnaWorkbook ReadWorkbook(Stream workbookStream)
    {
        using var workbook = new XLWorkbook(workbookStream);
        var rows = new List<DnaAccountRow>();
        var hardCodedRows = new List<DnaHardCodedRow>();
        foreach (var sheet in workbook.Worksheets.Where(s => !SkippedSheets.Contains(s.Name.Trim())))
        {
            var used = sheet.RangeUsed();
            if (used is null) continue;
            var header = FindHeader(sheet, used);
            if (header is null) continue;
            var (headerRow, columns, hardCoded) = header.Value;
            if (hardCoded || sheet.Name.Trim().Equals("HCC", StringComparison.OrdinalIgnoreCase) || sheet.Name.Contains("Hard-Coded", StringComparison.OrdinalIgnoreCase))
            {
                hardCodedRows.AddRange(ReadHardCoded(sheet, used, headerRow));
                continue;
            }
            if (rows.Count + (used.LastRow().RowNumber() - headerRow) > RiskScoring.CsvFileReader.MaxRows)
            {
                throw new InvalidOperationException($"The workbook has more than {RiskScoring.CsvFileReader.MaxRows:N0} account rows.");
            }

            var code = sheet.Name.Trim();
            // Account Type says it in both formats ("Domain: x", "Local", "Windows Domain User: x", ...); a sheet without that
            // column falls back to the account-list report's domain findings (-DA, DS-SPN) and the Domain Scan.
            var domainSheet = code.Contains("-DA", StringComparison.OrdinalIgnoreCase) || code.Equals("DS-SPN", StringComparison.OrdinalIgnoreCase)
                || code.Equals("Domain Scan", StringComparison.OrdinalIgnoreCase);
            for (var r = headerRow + 1; r <= used.LastRow().RowNumber(); r++)
            {
                string? Text(string name) => columns.TryGetValue(name, out var c) ? Blank(CellText(sheet.Cell(r, c))) : null;
                string? Starting(string prefix) => columns.Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .Select(kv => Blank(CellText(sheet.Cell(r, kv.Value)))).FirstOrDefault(v => v is not null);
                var account = Text("Account Name");
                if (account is null) continue;
                var accountType = Text("Account Type");
                var isDomain = accountType is null ? domainSheet : IsDomainType(accountType);
                rows.Add(new DnaAccountRow(
                    code, isDomain, Text("Machine Name"), account, Text("Account Display Name"),
                    accountType, Text("Account Category"), Text("Account Group"), Text("Privileged Domain Group"),
                    Text("Instance Name"), Text("Database Name"), Text("Compliance Status"), Text("Account State"),
                    YesNo(Text("Password Never Expires")),
                    columns.TryGetValue("Password Age", out var ageColumn) ? Whole(sheet.Cell(r, ageColumn)) : null,
                    columns.TryGetValue("Password Last Set", out var setColumn) ? Date(sheet.Cell(r, setColumn)) : null,
                    columns.TryGetValue("Last Login Date", out var loginColumn) ? Date(sheet.Cell(r, loginColumn)) : null,
                    columns.TryGetValue("Account Expiration Date", out var expiryColumn) ? Date(sheet.Cell(r, expiryColumn)) : null)
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
                });
            }
        }
        return new DnaWorkbook(rows, hardCodedRows);
    }

    /// <summary>
    /// D-345: the hard-coded credentials sheet. Its header is read again with every column (the account sheets drop any
    /// credential column); "Hard-Coded Credential" is kept only as DNA's masked text, masked again.
    /// </summary>
    private static IEnumerable<DnaHardCodedRow> ReadHardCoded(IXLWorksheet sheet, IXLRange used, int headerRow)
    {
        var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cell in sheet.Row(headerRow).CellsUsed())
        {
            var name = CellText(cell).Trim();
            if (name.Length > 0) columns.TryAdd(name, cell.Address.ColumnNumber);
        }
        var code = sheet.Name.Trim();
        for (var r = headerRow + 1; r <= used.LastRow().RowNumber(); r++)
        {
            string? Text(string name) => columns.TryGetValue(name, out var c) ? Blank(CellText(sheet.Cell(r, c))) : null;
            var file = Text("Hard-Coded in File");
            var account = Text("Account Name");
            var machine = Text("Machine Name");
            if (file is null && account is null && machine is null) continue;
            yield return new DnaHardCodedRow(
                code, machine, Text("Machine Type"), Text("Application Server"), Text("Application Name"), Text("Site Name"),
                account, file, MaskPasswords(Text("Hard-Coded Credential")),
                columns.TryGetValue("Password Length", out var lengthColumn) ? Whole(sheet.Cell(r, lengthColumn)) : null,
                Text("Target System Address"), Text("Target System Type"), Text("OS Version"), Text("Details"));
        }
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
    /// The header row (one of the first <see cref="HeaderSearchRows"/>) and its columns by name, credential columns left
    /// out; HardCoded when the header has "Hard-Coded in File", the column of a hard-coded credentials sheet.
    /// </summary>
    private static (int Row, Dictionary<string, int> Columns, bool HardCoded)? FindHeader(IXLWorksheet sheet, IXLRange used)
    {
        var first = used.FirstRow().RowNumber();
        for (var r = first; r < first + HeaderSearchRows && r <= used.LastRow().RowNumber(); r++)
        {
            var columns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var hardCoded = false;
            foreach (var cell in sheet.Row(r).CellsUsed())
            {
                var name = CellText(cell).Trim();
                if (name.Length == 0) continue;
                if (name.Equals("Hard-Coded in File", StringComparison.OrdinalIgnoreCase)) hardCoded = true;
                if (IsSecret(name)) continue;
                columns.TryAdd(name, cell.Address.ColumnNumber);
            }
            if (columns.ContainsKey("Account Name")) return (r, columns, hardCoded);
        }
        return null;
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
