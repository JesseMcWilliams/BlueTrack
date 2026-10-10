using System.Globalization;
using System.Text.RegularExpressions;

namespace BlueTrack.Api.Backups;

/// <summary>
/// D-200: which backup files a scheduled backup's clean-up may delete. Only
/// scheduled backups of this database, recognised by their exact name
/// (<c>&lt;database&gt;_scheduled_yyyyMMdd_HHmmss.bak</c>, UTC), and only when
/// that timestamp is older than the retention -- never a manual "Backup App"
/// file (<c>&lt;database&gt;_yyyyMMdd_HHmmss.bak</c>) or anything else in the folder.
/// </summary>
public static partial class BackupRetention
{
    public const string ScheduledMarker = "_scheduled_";

    public static string ScheduledFileName(string databaseName, DateTime utcNow) =>
        $"{databaseName}{ScheduledMarker}{utcNow:yyyyMMdd_HHmmss}.bak";

    /// <summary>The scheduled backup files in folder older than retentionDays, by the time in their name.</summary>
    public static IReadOnlyList<string> FilesToDelete(IEnumerable<string> fileNames, string databaseName, int retentionDays, DateTime utcNow)
    {
        var prefix = databaseName + ScheduledMarker;
        var cutoff = utcNow.AddDays(-retentionDays);
        var expired = new List<string>();
        foreach (var name in fileNames)
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var match = StampPattern().Match(name[prefix.Length..]);
            if (!match.Success) continue;
            var taken = DateTime.ParseExact(match.Groups[1].Value, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
            if (taken < cutoff) expired.Add(name);
        }
        return expired;
    }

    [GeneratedRegex(@"^(\d{8}_\d{6})\.bak$", RegexOptions.IgnoreCase)]
    private static partial Regex StampPattern();
}
