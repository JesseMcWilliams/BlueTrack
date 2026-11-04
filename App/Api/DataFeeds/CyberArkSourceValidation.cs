using BlueTrack.Api.Models;
using BlueTrack.Api.Security;

namespace BlueTrack.Api.DataFeeds;

/// <summary>
/// D-211: checks CyberArk source settings before they're saved or tested.
/// SQL Server's BULK INSERT needs an exact path, so a file name takes {date}
/// tokens (as a data feed's pattern does) but not the * and ? wildcards.
/// </summary>
public static class CyberArkSourceValidation
{
    /// <summary>
    /// The settings with surrounding spaces trimmed. With Self-Hosted off its PVWA address and credential are dropped.
    /// D-278: Self-Hosted Direct is the only Self-Hosted source.
    /// </summary>
    public static CyberArkSourceSettings Normalize(CyberArkSourceSettings s) => new()
    {
        ImportPrivilegeCloud = s.ImportPrivilegeCloud,
        ExportFolderPath = s.ImportPrivilegeCloud ? Blank(s.ExportFolderPath?.Trim().TrimEnd('\\')) : null,
        PlatformsFileName = s.PlatformsFileName?.Trim() ?? "",
        UsersFileName = s.UsersFileName?.Trim() ?? "",
        GroupsFileName = s.GroupsFileName?.Trim() ?? "",
        GroupMembersFileName = s.GroupMembersFileName?.Trim() ?? "",
        SafesFileName = s.SafesFileName?.Trim() ?? "",
        AccountsFileName = s.AccountsFileName?.Trim() ?? "",
        EntitlementsFileName = s.EntitlementsFileName?.Trim() ?? "",
        ImportSelfHosted = s.ImportSelfHosted,
        SelfHostedSource = "Direct",
        PvwaBaseUrl = s.ImportSelfHosted ? Blank(s.PvwaBaseUrl?.Trim().TrimEnd('/')) : null,
        PvwaFallbackUrls = s.ImportSelfHosted ? Blank(string.Join("\n", FallbackUrls(s.PvwaFallbackUrls))) : null,
        PvwaCredentialKey = s.ImportSelfHosted ? s.PvwaCredentialKey : null,
        PvwaLogonMethod = s.PvwaLogonMethod?.Trim() ?? "CyberArk",
        DirectPullTime = s.DirectPullTime?.Trim() ?? "01:30",
        DirectMaxChangePercent = s.DirectMaxChangePercent,
        DirectPullTimeLimitMinutes = s.DirectPullTimeLimitMinutes,
        DetailRefreshEnabled = s.DetailRefreshEnabled,
        DetailRefreshDays = s.DetailRefreshDays,
        DetailRefreshMaxAccounts = s.DetailRefreshMaxAccounts
    };

    private static bool IsValidPvwaUrl(string url) =>
        url.Length <= 400 && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && (uri.AbsolutePath is "/" || uri.AbsolutePath.Equals("/PasswordVault", StringComparison.OrdinalIgnoreCase));

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>D-273: the range the pull time limit may be set to, in minutes (5 minutes to 12 hours).</summary>
    public const int MinPullTimeLimitMinutes = 5, MaxPullTimeLimitMinutes = 720;

    /// <summary>D-299: the ranges of the account-detail re-read: every 1 to 365 days, 1 to 100,000 accounts a pull.</summary>
    public const int MaxDetailRefreshDays = 365, MaxDetailRefreshAccounts = 100_000;

    /// <summary>Every problem with the (normalized) settings; empty when they can be saved.</summary>
    /// <summary>D-320: how many other PVWA addresses Self-Hosted Direct may fail over to.</summary>
    public const int MaxFallbackUrls = 5;

    /// <summary>D-320: the other PVWA addresses, one per line (or comma-separated), trimmed, without a trailing slash or repeats.</summary>
    public static IReadOnlyList<string> FallbackUrls(string? value) =>
        (value ?? "").Split(['\n', '\r', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(u => u.TrimEnd('/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static IReadOnlyList<string> Validate(CyberArkSourceSettings s, AllowedFileRoots allowedFileRoots)
    {
        var errors = new List<string>();
        if (!s.ImportPrivilegeCloud && !s.ImportSelfHosted)
        {
            errors.Add("Turn on at least one source.");
        }

        if (s.ImportPrivilegeCloud)
        {
            if (s.ExportFolderPath is null) errors.Add("Export folder is required for Privilege Cloud.");
            else if (s.ExportFolderPath.Length > 400) errors.Add("Export folder must be 400 characters or fewer.");
            else if (allowedFileRoots.Check(s.ExportFolderPath) is { } folderError) errors.Add(folderError);   // D-197
        }

        // File names are kept even with Privilege Cloud off, so they must always be valid.
        foreach (var (_, label, fileName) in s.Files())
        {
            if (FeedFileResolver.ValidatePattern(fileName) is { } patternError) errors.Add($"{label} file: {patternError}");
            else if (fileName.Contains('*') || fileName.Contains('?')) errors.Add($"{label} file: * and ? aren't allowed; SQL Server needs the exact file name.");
            else if (fileName.Length > 200) errors.Add($"{label} file: must be 200 characters or fewer.");
        }

        if (s.PvwaLogonMethod is not ("CyberArk" or "LDAP")) errors.Add("PVWA logon method must be CyberArk or LDAP.");
        if (!FeedSchedule.TryParseTime(s.DirectPullTime, out _)) errors.Add("Pull time must be a time as HH:mm (24-hour).");
        if (s.DirectMaxChangePercent is < 0 or > 100) errors.Add("Allowed change must be between 0 and 100 percent.");
        if (s.DirectPullTimeLimitMinutes is < MinPullTimeLimitMinutes or > MaxPullTimeLimitMinutes)
            errors.Add($"Pull time limit must be between {MinPullTimeLimitMinutes} and {MaxPullTimeLimitMinutes} minutes.");
        if (s.DetailRefreshDays is < 1 or > MaxDetailRefreshDays)
            errors.Add($"Re-read account details must be every 1 to {MaxDetailRefreshDays} days.");
        if (s.DetailRefreshMaxAccounts is < 1 or > MaxDetailRefreshAccounts)
            errors.Add($"Accounts re-read per pull must be between 1 and {Formatting.ServerTextCulture.Count(MaxDetailRefreshAccounts)}.");

        if (s.ImportSelfHosted)
        {
            if (s.PvwaBaseUrl is null) errors.Add("PVWA address is required for Self-Hosted Direct.");
            else if (!IsValidPvwaUrl(s.PvwaBaseUrl))
                errors.Add("PVWA address must be https://host, optionally ending in /PasswordVault (400 characters at most).");
            if (s.PvwaCredentialKey is null) errors.Add("A credential is required for Self-Hosted Direct.");
            var fallbacks = FallbackUrls(s.PvwaFallbackUrls);
            if (fallbacks.Count > MaxFallbackUrls) errors.Add($"At most {MaxFallbackUrls} other PVWA addresses.");
            var bad = fallbacks.Where(u => !IsValidPvwaUrl(u)).ToList();
            if (bad.Count > 0) errors.Add($"Each other PVWA address must be https://host, optionally ending in /PasswordVault: {string.Join(", ", bad)}.");
        }
        return errors;
    }
}
