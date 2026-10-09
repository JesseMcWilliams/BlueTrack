using System.Globalization;
using System.Text.RegularExpressions;

namespace BlueTrack.Api.DataFeeds;

/// <summary>
/// D-181: finds the file a feed should import. The pattern is a file name
/// (no folder) that may contain {date-format} tokens, filled in with today's
/// date (e.g. "targets_{yyyy-MM-dd}.csv"), and the * and ? wildcards. When
/// more than one file matches, the newest (by last write time) wins. Files
/// are left in place after import.
/// </summary>
public static partial class FeedFileResolver
{
    [GeneratedRegex(@"\{([^{}]+)\}")]
    private static partial Regex DateToken();

    /// <summary>The pattern with each {format} token replaced by <paramref name="today"/> in that format.</summary>
    public static string ExpandPattern(string pattern, DateTime today) =>
        DateToken().Replace(pattern, m => today.ToString(m.Groups[1].Value, CultureInfo.InvariantCulture));

    /// <summary>Null when the pattern is usable; otherwise why it isn't.</summary>
    public static string? ValidatePattern(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return "File name pattern is required.";
        if (pattern.Contains('/') || pattern.Contains('\\')) return "File name pattern must be a file name only; put the folder in Folder path.";
        if (pattern.Contains("..")) return "File name pattern must not contain '..'.";
        string expanded;
        try
        {
            expanded = ExpandPattern(pattern, DateTime.Today);
        }
        catch (FormatException)
        {
            return "File name pattern has a {…} token that isn't a valid date format, e.g. {yyyy-MM-dd}.";
        }
        if (expanded.IndexOfAny(Path.GetInvalidFileNameChars().Where(c => c is not '*' and not '?').ToArray()) >= 0)
            return "File name pattern contains characters that aren't allowed in a file name.";
        return null;
    }

    /// <summary>Every file in <paramref name="folderPath"/> matching the pattern, newest first.</summary>
    public static IReadOnlyList<FileInfo> FindMatches(string folderPath, string pattern, DateTime today)
    {
        var expanded = ExpandPattern(pattern, today);
        var options = new EnumerationOptions { MatchType = MatchType.Simple, MatchCasing = MatchCasing.CaseInsensitive, RecurseSubdirectories = false };
        return new DirectoryInfo(folderPath).EnumerateFiles(expanded, options)
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .ThenByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
