using System.Text;
using BlueTrack.Api.Models;
using BlueTrack.Api.Reports;
using ClosedXML.Excel;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-220: the CSV and .xlsx writers behind every report export, and the two reports whose columns are built rather than listed.</summary>
public class ReportExportWriterTests
{
    private static ReportTable Sample() => new(
        ["Name", "Score", "When", "Notes"],
        [
            ["plain", 42, new DateOnly(2026, 10, 13), null],
            ["has, comma", -5, null, "say \"hi\""],
            ["line\r\nbreak", 1234567L, new DateOnly(2026, 1, 2), "=1+1"],
        ]);

    private static string CsvText(byte[] bytes) => new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);

    [Fact]
    public void Csv_StartsWithAUtf8Bom_AndEndsEveryRecordWithCrLf()
    {
        var bytes = CsvReportWriter.Write(Sample());

        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var text = CsvText(bytes);
        Assert.StartsWith("Name,Score,When,Notes\r\n", text);
        Assert.EndsWith("\r\n", text);
    }

    [Fact]
    public void Csv_QuotesFieldsHoldingCommasQuotesOrLineBreaks_PerRfc4180()
    {
        var text = CsvText(CsvReportWriter.Write(Sample()));

        Assert.Contains("plain,42,2026-10-13,\r\n", text);
        Assert.Contains("\"has, comma\",-5,,\"say \"\"hi\"\"\"\r\n", text);
        Assert.Contains("\"line\r\nbreak\",1234567,2026-01-02,'=1+1\r\n", text);
    }

    [Theory]
    [InlineData("=SUM(A1:A2)", "'=SUM(A1:A2)")]
    [InlineData("+1", "'+1")]
    [InlineData("-2", "'-2")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("\tx", "'\tx")]
    [InlineData("a=b", "a=b")]
    [InlineData("", "")]
    public void Csv_GuardsTextThatASpreadsheetWouldRunAsAFormula(string value, string expected)
    {
        Assert.Equal(expected, CsvReportWriter.GuardFormula(value));
    }

    [Fact]
    public void Csv_GuardsALeadingCarriageReturn_AndQuotesIt()
    {
        Assert.Equal("\"'\rx\"", CsvReportWriter.Field("\rx"));
    }

    [Fact]
    public void Csv_DoesNotGuardNumbers_WhichTheWriterFormatsItself()
    {
        Assert.Equal("-5", CsvReportWriter.Field(-5));
        Assert.Equal("1.5", CsvReportWriter.Field(1.5));
    }

    private static XLWorkbook OpenXlsx(byte[] bytes) => new(new MemoryStream(bytes));

    [Fact]
    public void Xlsx_HasABoldFrozenHeaderRowWithAnAutoFilter()
    {
        using var workbook = OpenXlsx(XlsxReportWriter.Write(Sample(), "Risk Score"));
        var sheet = workbook.Worksheet(1);

        Assert.Equal("Risk Score", sheet.Name);
        Assert.Equal(["Name", "Score", "When", "Notes"], Enumerable.Range(1, 4).Select(c => sheet.Cell(1, c).GetString()));
        Assert.True(sheet.Cell(1, 1).Style.Font.Bold);
        Assert.Equal(1, sheet.SheetView.SplitRow);
        Assert.True(sheet.AutoFilter.IsEnabled);
        Assert.Equal("A1:D4", sheet.AutoFilter.Range.RangeAddress.ToString());
    }

    [Fact]
    public void Xlsx_WritesTextAsText_NeverAsAFormula()
    {
        using var workbook = OpenXlsx(XlsxReportWriter.Write(Sample(), "Sheet"));
        var formulaLike = workbook.Worksheet(1).Cell(4, 4);

        Assert.False(formulaLike.HasFormula);
        Assert.Equal(XLDataType.Text, formulaLike.DataType);
        Assert.Equal("=1+1", formulaLike.GetString());
    }

    [Fact]
    public void Xlsx_WritesNumbersAndDatesAsTypedCells()
    {
        using var workbook = OpenXlsx(XlsxReportWriter.Write(Sample(), "Sheet"));
        var sheet = workbook.Worksheet(1);

        Assert.Equal(XLDataType.Number, sheet.Cell(2, 2).DataType);
        Assert.Equal(42, sheet.Cell(2, 2).GetDouble());
        Assert.Equal(1234567, sheet.Cell(4, 2).GetDouble());
        Assert.Equal(XLDataType.DateTime, sheet.Cell(2, 3).DataType);
        Assert.Equal(new DateTime(2026, 10, 13), sheet.Cell(2, 3).GetDateTime());
        Assert.Equal("yyyy-mm-dd", sheet.Cell(2, 3).Style.NumberFormat.Format);
        Assert.True(sheet.Cell(3, 3).IsEmpty());
        Assert.Equal(XLDataType.Text, sheet.Cell(3, 1).DataType);
    }

    /// <summary>D-231: a DateTime (the Audit Log's timestamps) keeps its time -- "yyyy-MM-dd HH:mm:ss" in CSV, a date-time cell in Excel.</summary>
    [Fact]
    public void DateTimeCells_KeepTheirTime_InCsvAndXlsx()
    {
        var table = new ReportTable(["When"], [[new DateTime(2026, 10, 14, 13, 5, 9)], [null]]);

        Assert.Contains("When\r\n2026-10-14 13:05:09\r\n", CsvText(CsvReportWriter.Write(table)));

        using var workbook = OpenXlsx(XlsxReportWriter.Write(table, "Sheet"));
        var cell = workbook.Worksheet(1).Cell(2, 1);
        Assert.Equal(XLDataType.DateTime, cell.DataType);
        Assert.Equal(new DateTime(2026, 10, 14, 13, 5, 9), cell.GetDateTime());
        Assert.Equal("yyyy-mm-dd hh:mm:ss", cell.Style.NumberFormat.Format);
    }

    /// <summary>D-231: the Audit Log's columns -- the viewer grid's, with Occurred At cut to the second and Entity as "name key".</summary>
    [Fact]
    public void AuditLogColumns_MatchTheViewersGrid()
    {
        var row = new AuditEventSummary
        {
            AuditEventKey = 7,
            EventTypeName = "FieldEdit",
            OccurredAt = new DateTime(2026, 10, 14, 13, 5, 9).AddTicks(1234567),
            PerformedByName = "Jane Analyst",
            EntityName = "fact_account_progress",
            EntityKey = "42",
            Detail = "Stage changed",
            Reason = "not exported"
        };
        var table = ReportTable.From(ReportExports.AuditLogColumns, [row, new AuditEventSummary { EventTypeName = "Logon", EntityName = "app_user" }]);

        Assert.Equal(["Occurred At (UTC)", "Event Type", "By", "Entity", "Detail"], table.Headers);
        Assert.Equal(new object?[] { new DateTime(2026, 10, 14, 13, 5, 9), "FieldEdit", "Jane Analyst", "fact_account_progress 42", "Stage changed" }, table.Rows[0]);
        Assert.Equal("app_user", table.Rows[1][3]);
    }

    [Fact]
    public void Xlsx_KeepsNumberLookingTextAsText()
    {
        var table = new ReportTable(["Id"], [["00123"]]);
        using var workbook = OpenXlsx(XlsxReportWriter.Write(table, "Sheet"));
        var cell = workbook.Worksheet(1).Cell(2, 1);

        Assert.Equal(XLDataType.Text, cell.DataType);
        Assert.Equal("00123", cell.GetString());
    }

    [Fact]
    public void Xlsx_WithNoRows_StillWritesTheHeader()
    {
        using var workbook = OpenXlsx(XlsxReportWriter.Write(new ReportTable(["A", "B"], []), "Empty"));
        Assert.Equal("B", workbook.Worksheet(1).Cell(1, 2).GetString());
    }

    [Theory]
    [InlineData("Overdue / At-Risk", "Overdue - At-Risk")]
    [InlineData("A:B\\C?D*E[F]", "A-B-C-D-E-F-")]
    [InlineData("Accounts Flagged for Deletion Report", "Accounts Flagged for Deletion R")]
    [InlineData("  ", "Report")]
    public void SheetName_DropsCharactersExcelRefuses_AndFitsIn31(string name, string expected)
    {
        Assert.Equal(expected, XlsxReportWriter.SheetName(name));
    }

    [Fact]
    public void ReportTable_RefusesARowWithTheWrongNumberOfCells()
    {
        Assert.Throws<ArgumentException>(() => new ReportTable(["A", "B"], [["only one"]]));
    }

    [Fact]
    public void StageStatusTable_IsTheGridsPivot_StagesInOrderAndATotal()
    {
        var table = ReportExports.StageStatusTable(
        [
            new StageStatusFunnelRow { StageOrder = 2, StageName = "Onboarded", StatusName = "Complete", AccountCount = 3 },
            new StageStatusFunnelRow { StageOrder = 2, StageName = "Onboarded", StatusName = "In Progress", AccountCount = 1 },
            new StageStatusFunnelRow { StageOrder = 1, StageName = "Discovered", StatusName = "Complete", AccountCount = 0 },
            new StageStatusFunnelRow { StageOrder = 1, StageName = "Discovered", StatusName = "In Progress", AccountCount = 7 },
        ]);

        Assert.Equal(["Stage", "Complete", "In Progress", "Total"], table.Headers);
        Assert.Equal(new object?[] { "Discovered", 0, 7, 7 }, table.Rows[0]);
        Assert.Equal(new object?[] { "Onboarded", 3, 1, 4 }, table.Rows[1]);
    }

    [Fact]
    public void KpiTable_ShowsCountsAsOnThePage_AndNotApplicableWithNoDenominator()
    {
        var table = ReportExports.KpiTable(new KpiSummary
        {
            TotalAccounts = 3, InScopeAccounts = 2, OnboardedAccounts = 1, ManagedAccounts = 0, CompliantAccounts = 0
        });

        Assert.Equal(["KPI", "Count", "Percentage"], table.Headers);
        Assert.Equal("2 / 3", table.Rows[0][1]);
        Assert.Equal(66.7, table.Rows[0][2]);
        Assert.Equal("N/A", table.Rows[3][2]);
    }

    [Fact]
    public void DecomAccounts_SpellsOutFlagsAndTheNote_AsTheGridDoes()
    {
        var row = ReportTable.From(ReportExports.DecomAccountsColumns,
        [
            new DecomAccountRow { AccountKey = 1, AccountName = "acct", UserName = null, SourceSystemName = "PC",
                InFlaggedSafe = true, NameFlagged = true, IsDeleted = false, OtherSafes = "SafeB" }
        ]).Rows[0];

        Assert.Equal("acct (no username)", row[0]);
        Assert.Equal("Safe flagged, Name flagged", row[5]);
        Assert.Equal("Not deleted yet. Also in: SafeB", row[6]);
    }
}
