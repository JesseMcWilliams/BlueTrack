namespace BlueTrack.Api.Models;

/// <summary>
/// D-211 (Data Sources phase 3): the web.cyberark_source_config row that
/// dbo.usp_Import_Configured reads each night -- which CyberArk sources are
/// imported, the Privilege Cloud export folder and file names, and the
/// Self-Hosted EVD database. Also the body of a save or a Test. File names
/// may contain {format} date tokens; the defaults are the script's own.
/// </summary>
public sealed class CyberArkSourceSettings
{
    public bool ImportPrivilegeCloud { get; init; }
    public string? ExportFolderPath { get; init; }
    public string PlatformsFileName { get; init; } = "Export_PlatformsList.csv";
    public string UsersFileName { get; init; } = "Export_UsersList.csv";
    public string GroupsFileName { get; init; } = "Export_GroupsList.csv";
    public string GroupMembersFileName { get; init; } = "Export Local Group Members {yyyy-MM-dd}.csv";
    public string SafesFileName { get; init; } = "Export_SafesList.csv";
    public string AccountsFileName { get; init; } = "Export_AccountsList.csv";
    public string EntitlementsFileName { get; init; } = "Export Entitlements {yyyy-MM-dd}.csv";
    public bool ImportSelfHosted { get; init; }
    public string? EvdDatabaseName { get; init; }

    /// <summary>D-248: where Self-Hosted comes from: "Evd" (the EVD database, the default) or "Direct" (PVWA's REST API).</summary>
    public string SelfHostedSource { get; init; } = "Evd";
    /// <summary>Direct: the PVWA address, https://host (the /PasswordVault part is optional).</summary>
    public string? PvwaBaseUrl { get; init; }
    /// <summary>Direct: the named credential (web.credential; a CyberArk CP or CCP one fetches it from the Vault at each pull).</summary>
    public int? PvwaCredentialKey { get; init; }
    /// <summary>Direct: "CyberArk" or "LDAP" authentication; both take a user name and password.</summary>
    public string PvwaLogonMethod { get; init; } = "CyberArk";
    /// <summary>Direct: when the API pulls each night, HH:mm server time. It must come before the Import job (default 02:00).</summary>
    public string DirectPullTime { get; init; } = "01:30";
    /// <summary>Direct: the most the Safe or account count may change since the last good pull, as a percent, before the pull is rejected.</summary>
    public int DirectMaxChangePercent { get; init; } = 20;

    /// <summary>The seven Privilege Cloud files, with the labels the page and Test show.</summary>
    public IEnumerable<(string Field, string Label, string FileName)> Files() =>
    [
        (nameof(PlatformsFileName), "Platforms", PlatformsFileName),
        (nameof(UsersFileName), "Users", UsersFileName),
        (nameof(GroupsFileName), "Groups", GroupsFileName),
        (nameof(GroupMembersFileName), "Local group members", GroupMembersFileName),
        (nameof(SafesFileName), "Safes", SafesFileName),
        (nameof(AccountsFileName), "Accounts", AccountsFileName),
        (nameof(EntitlementsFileName), "Entitlements", EntitlementsFileName)
    ];
}

/// <summary>GET response: the saved settings, or the defaults with IsConfigured false when no row exists yet.</summary>
public sealed class CyberArkSourceSettingsResponse
{
    public bool IsConfigured { get; init; }
    /// <summary>The source options the page offers, per platform (D-248); an unavailable one is shown as planned.</summary>
    public IReadOnlyList<CyberArkSourceOption> Options { get; init; } = CyberArkSourceOption.All;
    public required CyberArkSourceSettings Settings { get; init; }
    public DateTime? ModifiedDate { get; init; }
    public string? ModifiedByName { get; init; }
}

public sealed class CyberArkSourceTestCheck
{
    public required string Item { get; init; }
    public bool Ok { get; init; }
    public required string Message { get; init; }
}

/// <summary>D-248: one way to get a platform's data into BlueTrack. EVD and Privilege Cloud Folder import through SQL; the Direct ones are pulled by the API.</summary>
public sealed record CyberArkSourceOption(string Key, string Platform, string Label, bool Available)
{
    public static readonly IReadOnlyList<CyberArkSourceOption> All =
    [
        new("SelfHostedEvd", "SelfHosted", "EVD database", true),
        new("SelfHostedDirect", "SelfHosted", "Self-Hosted Direct (PVWA REST API)", true),
        new("PrivilegeCloudFolder", "PrivilegeCloud", "Privilege Cloud export folder", true),
        new("PrivilegeCloudDirect", "PrivilegeCloud", "Privilege Cloud Direct (planned)", false)
    ];
}
