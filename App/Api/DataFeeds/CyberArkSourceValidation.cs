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

    /// <summary>The settings with surrounding spaces trimmed and an unused source's value dropped.</summary>
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
        SelfHostedSource = s.SelfHostedSource?.Trim() ?? "Evd",
        EvdDatabaseName = s.ImportSelfHosted && IsEvd(s) ? Blank(s.EvdDatabaseName?.Trim()) : null,
        PvwaBaseUrl = s.ImportSelfHosted && !IsEvd(s) ? Blank(s.PvwaBaseUrl?.Trim().TrimEnd('/')) : null,
        PvwaCredentialKey = s.ImportSelfHosted && !IsEvd(s) ? s.PvwaCredentialKey : null,
        PvwaLogonMethod = s.PvwaLogonMethod?.Trim() ?? "CyberArk",
        DirectPullTime = s.DirectPullTime?.Trim() ?? "01:30",
        DirectMaxChangePercent = s.DirectMaxChangePercent
    };

    private static bool IsEvd(CyberArkSourceSettings s) => !string.Equals(s.SelfHostedSource?.Trim(), "Direct", StringComparison.Ordinal);

    private static string? Blank(string? value) => string.IsNullOrEmpty(value) ? null : value;

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

        if (s.ImportSelfHosted && s.SelfHostedSource == "Direct")
        {
            if (s.PvwaBaseUrl is null) errors.Add("PVWA address is required for Self-Hosted Direct.");
            else if (s.PvwaBaseUrl.Length > 400 || !Uri.TryCreate(s.PvwaBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
                     || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
                     || !(uri.AbsolutePath is "/" || uri.AbsolutePath.Equals("/PasswordVault", StringComparison.OrdinalIgnoreCase)))
                errors.Add("PVWA address must be https://host, optionally ending in /PasswordVault (400 characters at most).");
            if (s.PvwaCredentialKey is null) errors.Add("A credential is required for Self-Hosted Direct.");
        }
        else if (s.ImportSelfHosted)
        {
            if (s.EvdDatabaseName is null) errors.Add("EVD database is required for Self-Hosted.");
            else if (s.EvdDatabaseName.Length > 128 || !EvdName().IsMatch(s.EvdDatabaseName))
                errors.Add("EVD database name may contain only letters, digits and underscores (128 at most).");
        }
        return errors;
    }
}
