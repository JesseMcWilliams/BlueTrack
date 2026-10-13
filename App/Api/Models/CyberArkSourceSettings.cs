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
