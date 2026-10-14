namespace BlueTrack.Api.DataFeeds;

/// <summary>
/// D-228: one row of web.cyberark_import_column (22_BlueTrack_PcImportByHeader.sql),
/// the list of each Privilege Cloud file's columns. The nightly import
/// (dbo.usp_Import_PC_ReadCsvByHeader) reads the same rows, so Test and the
/// import agree on what a file needs.
/// </summary>
public sealed record CyberArkImportColumn(string FileKind, string ColumnName, bool IsRequired, int ColumnOrder);

/// <summary>D-228: compares a file's header with its kind's columns, as the import does.</summary>
public static class CyberArkImportColumns
{
    private const string FileNameSuffix = "FileName";

    /// <summary>
    /// The FileKind for a settings field (UsersFileName is Users,
    /// GroupMembersFileName is GroupMembers), as in the table.
    /// </summary>
    public static string FileKindOf(string settingsField) =>
        settingsField.EndsWith(FileNameSuffix, StringComparison.Ordinal) ? settingsField[..^FileNameSuffix.Length] : settingsField;

    /// <summary>
    /// The required and the optional columns of this kind that the header
    /// lacks, in the table's order. Names match ignoring case and surrounding
    /// spaces, as in SQL; columns the table doesn't list are ignored.
    /// </summary>
    public static (IReadOnlyList<string> MissingRequired, IReadOnlyList<string> MissingOptional) Compare(
        IEnumerable<string> header, IEnumerable<CyberArkImportColumn> columnsOfKind)
    {
        var present = new HashSet<string>(header.Select(h => h.Trim()), StringComparer.OrdinalIgnoreCase);
        var absent = columnsOfKind.OrderBy(c => c.ColumnOrder).Where(c => !present.Contains(c.ColumnName)).ToList();
        return (absent.Where(c => c.IsRequired).Select(c => c.ColumnName).ToList(),
                absent.Where(c => !c.IsRequired).Select(c => c.ColumnName).ToList());
    }
}
