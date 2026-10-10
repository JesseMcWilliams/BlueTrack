namespace BlueTrack.Api.Security;

/// <summary>
/// D-197: the folders BlueTrack may read data-feed files from or have SQL
/// Server write backups to. Both paths are typed on admin pages, and the
/// server then acts on them as itself: the app pool reads a feed folder, SQL
/// Server writes a full database backup to the backup folder. An unrestricted
/// path let an admin page read any file the app pool can, copy the database
/// anywhere, and -- with \\attacker\share -- hand the machine's NTLM
/// credentials to another host.
///
/// The limit is a server-side setting, not something the web pages can
/// change: FileAccess:AllowedRoots in appsettings.&lt;Environment&gt;.json
/// (the installer's -AllowedFileRoots). With roots set, a path must be one of
/// them or inside one. Without, any local path is allowed and network (UNC)
/// paths are refused.
/// </summary>
public sealed class AllowedFileRoots
{
    private readonly IReadOnlyList<string> roots;

    public AllowedFileRoots(IConfiguration configuration)
    {
        roots = (configuration.GetSection("FileAccess:AllowedRoots").Get<string[]>() ?? [])
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(Normalize)
            .ToList();
    }

    /// <summary>Null if the folder may be used; otherwise why not, to show the admin.</summary>
    public string? Check(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
        {
            return "The folder must be a full path, e.g. D:\\Feeds.";
        }

        string normalized;
        try
        {
            normalized = Normalize(folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "The folder path isn't valid.";
        }

        if (roots.Count > 0)
        {
            return roots.Any(root => IsSameOrInside(normalized, root))
                ? null
                : $"The folder must be inside one of the allowed folders set on the server ({string.Join("; ", roots)}). "
                  + "An administrator can add one with the installer's -AllowedFileRoots (FileAccess:AllowedRoots).";
        }

        return normalized.StartsWith(@"\\", StringComparison.Ordinal)
            ? "Network (UNC) folders are allowed only when the server lists them in FileAccess:AllowedRoots "
              + "(the installer's -AllowedFileRoots). Use a local folder, or ask an administrator to allow this share."
            : null;
    }

    // Full path with "." and ".." resolved and no trailing separator, so
    // D:\Feeds\..\Windows can't pass as inside D:\Feeds.
    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));

    private static bool IsSameOrInside(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
