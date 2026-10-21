using System.Text.RegularExpressions;
using BlueTrack.Api.Models;
using BlueTrack.Api.Security;

namespace BlueTrack.Api.DataFeeds;

/// <summary>
/// D-211: checks CyberArk source settings before they're saved or tested.
/// SQL Server's BULK INSERT needs an exact path, so a file name takes {date}
/// tokens (as a data feed's pattern does) but not the * and ? wildcards.
/// </summary>
public static partial class CyberArkSourceValidation
{
    [GeneratedRegex("^[A-Za-z0-9_]+$")]
    private static partial Regex EvdName();

    /// <summary>
    /// The settings with surrounding spaces trimmed. With Self-Hosted on, the source not in use keeps its values when
    /// they are valid (D-270), so switching between the EVD and Direct, or back, needs nothing typed again; an invalid
    /// value of the source not in use is dropped, since its field is hidden and couldn't be corrected.
    /// D-271: the shadow pull is on only with the EVD as the source; while it is on, the PVWA settings are in use.
    /// </summary>
    public static CyberArkSourceSettings Normalize(CyberArkSourceSettings s)
    {
        var evd = Blank(s.EvdDatabaseName?.Trim());
        var pvwa = Blank(s.PvwaBaseUrl?.Trim().TrimEnd('/'));
        var shadow = s.ImportSelfHosted && IsEvd(s) && s.ShadowPullEnabled;
        return Normalized(s,
            evdDatabaseName: !s.ImportSelfHosted ? null : IsEvd(s) || evd is null || IsValidEvdName(evd) ? evd : null,
            pvwaBaseUrl: !s.ImportSelfHosted ? null : !IsEvd(s) || shadow || pvwa is null || IsValidPvwaUrl(pvwa) ? pvwa : null,
            shadow);
    }

    private static CyberArkSourceSettings Normalized(CyberArkSourceSettings s, string? evdDatabaseName, string? pvwaBaseUrl, bool shadowPullEnabled) => new()
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
        SelfHostedSource = s.SelfHostedSource?.Trim() ?? "Evd",
        EvdDatabaseName = evdDatabaseName,
        PvwaBaseUrl = pvwaBaseUrl,
        PvwaCredentialKey = s.ImportSelfHosted ? s.PvwaCredentialKey : null,
        PvwaLogonMethod = s.PvwaLogonMethod?.Trim() ?? "CyberArk",
        DirectPullTime = s.DirectPullTime?.Trim() ?? "01:30",
        DirectMaxChangePercent = s.DirectMaxChangePercent,
        DirectPullTimeLimitMinutes = s.DirectPullTimeLimitMinutes,
        ShadowPullEnabled = shadowPullEnabled,
        ShadowPullTime = s.ShadowPullTime?.Trim() ?? "04:00"
    };

    private static bool IsEvd(CyberArkSourceSettings s) => !string.Equals(s.SelfHostedSource?.Trim(), "Direct", StringComparison.Ordinal);

    private static bool IsValidEvdName(string name) => name.Length <= 128 && EvdName().IsMatch(name);

    private static bool IsValidPvwaUrl(string url) =>
        url.Length <= 400 && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && (uri.AbsolutePath is "/" || uri.AbsolutePath.Equals("/PasswordVault", StringComparison.OrdinalIgnoreCase));

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>D-273: the range the pull time limit may be set to, in minutes (5 minutes to 12 hours).</summary>
    public const int MinPullTimeLimitMinutes = 5, MaxPullTimeLimitMinutes = 720;

    /// <summary>Every problem with the (normalized) settings; empty when they can be saved.</summary>
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

        if (s.SelfHostedSource is not ("Evd" or "Direct")) errors.Add("Self-Hosted source must be Evd or Direct.");
        if (s.PvwaLogonMethod is not ("CyberArk" or "LDAP")) errors.Add("PVWA logon method must be CyberArk or LDAP.");
        if (!FeedSchedule.TryParseTime(s.DirectPullTime, out _)) errors.Add("Pull time must be a time as HH:mm (24-hour).");
        if (s.DirectMaxChangePercent is < 0 or > 100) errors.Add("Allowed change must be between 0 and 100 percent.");
        if (s.DirectPullTimeLimitMinutes is < MinPullTimeLimitMinutes or > MaxPullTimeLimitMinutes)
            errors.Add($"Pull time limit must be between {MinPullTimeLimitMinutes} and {MaxPullTimeLimitMinutes} minutes.");
        if (!FeedSchedule.TryParseTime(s.ShadowPullTime, out _)) errors.Add("Shadow pull time must be a time as HH:mm (24-hour).");

        if (s.ImportSelfHosted && (s.SelfHostedSource == "Direct" || s.ShadowPullEnabled))
        {
            var forWhat = s.SelfHostedSource == "Direct" ? "Self-Hosted Direct" : "the shadow pull";
            if (s.PvwaBaseUrl is null) errors.Add($"PVWA address is required for {forWhat}.");
            else if (!IsValidPvwaUrl(s.PvwaBaseUrl))
                errors.Add("PVWA address must be https://host, optionally ending in /PasswordVault (400 characters at most).");
            if (s.PvwaCredentialKey is null) errors.Add($"A credential is required for {forWhat}.");
        }
        if (s.ImportSelfHosted && s.SelfHostedSource != "Direct")
        {
            if (s.EvdDatabaseName is null) errors.Add("EVD database is required for Self-Hosted.");
            else if (!IsValidEvdName(s.EvdDatabaseName))
                errors.Add("EVD database name may contain only letters, digits and underscores (128 at most).");
        }
        return errors;
    }
}
