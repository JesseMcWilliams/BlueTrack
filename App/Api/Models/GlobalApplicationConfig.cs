namespace BlueTrack.Api.Models;

/// <summary>
/// Merges web.app_config and web.audit_config (both singleton tables) into
/// one shape for the Global Application Configuration admin page --
/// D-28 (idle timeout), D-57 (breadcrumb position), D-17 (exception ID
/// pattern), D-12/D-35 (audit retention / read-event logging).
/// </summary>
public sealed class GlobalApplicationConfig
{
    public int IdleTimeoutMinutes { get; init; }
    public required string BreadcrumbPosition { get; init; }
    public required string ExceptionIdPattern { get; init; }
    public int LockTimeoutMinutes { get; init; }
    public int? RetentionDays { get; init; }
    public bool LogReadEvents { get; init; }

    /// <summary>D-117: BACKUP DATABASE's TO DISK target directory for the Deployment page's Backup App button.</summary>
    public string? BackupFolder { get; init; }

    /// <summary>D-119 Phase D: which of the two candidate scoring algorithms usp_CalculateRiskScore dispatches to.</summary>
    public required string ActiveRiskAlgorithm { get; init; }

    /// <summary>
    /// Segregation of duties: when true, AccountProgressController.Update
    /// rejects linking a Risk Exception to an account if the current user
    /// is also that exception's ApprovedBy. Off by default -- some
    /// organizations don't have enough staff to separate the two roles.
    /// </summary>
    public bool EnforceRiskExceptionSegregationOfDuties { get; init; }

    // D-181 (Data Sources phase 2): when the nightly data feeds run, how
    // long their run history is kept, and the business hours inside which
    // Run now warns. Times are "HH:mm", server local time; BusinessDays is
    // comma-separated three-letter English day names ("Mon,Tue,...").
    public required string DataFeedRunTime { get; init; }
    public int DataFeedRunRetentionDays { get; init; }

    /// <summary>D-200: daily scheduled backup time ("HH:mm", server local time); null = off.</summary>
    public string? ScheduledBackupTime { get; init; }

    /// <summary>D-200: scheduled backup files older than this are deleted after each scheduled backup.</summary>
    public int ScheduledBackupRetentionDays { get; init; }
    public required string BusinessHoursStart { get; init; }
    public required string BusinessHoursEnd { get; init; }
    public required string BusinessDays { get; init; }

    /// <summary>D-182: the most accounts one Account Progress bulk edit may change.</summary>
    public int BulkEditMaxAccounts { get; init; }

    /// <summary>D-220: the most rows one report export (CSV or Excel) may hold.</summary>
    public int ExportMaxRows { get; init; }

    /// <summary>D-243: the expiry given to a new API key, in days (1 to 365).</summary>
    public int ApiKeyDefaultLifetimeDays { get; init; }

    /// <summary>D-243: the most requests one API key may make in a minute.</summary>
    public int ApiRateLimitPerMinute { get; init; }

    // D-186: decommission and ignored-safe name patterns (Mode: Off / Prefix
    // / Suffix / Regex). RegexSupported is read-only: whether this SQL
    // Server can run Regex patterns (2025, compatibility level 170).
    public required string SafeDecomMode { get; init; }
    public string? SafeDecomValue { get; init; }
    public required string AccountDecomMode { get; init; }
    public string? AccountDecomValue { get; init; }
    public required string SafeIgnoreMode { get; init; }
    public string? SafeIgnoreValue { get; init; }
    public bool RegexSupported { get; init; }

    /// <summary>D-224: the address people open BlueTrack at (e.g. https://bluetrack.company.com); emails link to pages under it. Null = not set.</summary>
    public string? SiteUrl { get; init; }
}

public sealed class SaveGlobalApplicationConfigRequest
{
    public int IdleTimeoutMinutes { get; init; }
    public required string BreadcrumbPosition { get; init; }
    public required string ExceptionIdPattern { get; init; }
    public int LockTimeoutMinutes { get; init; }
    public int? RetentionDays { get; init; }
    public bool LogReadEvents { get; init; }
    public string? BackupFolder { get; init; }
    public required string ActiveRiskAlgorithm { get; init; }
    public bool EnforceRiskExceptionSegregationOfDuties { get; init; }

    // D-181: optional, so a caller sending the older shape leaves these
    // settings as they are (null = unchanged), rather than resetting them.
    public string? DataFeedRunTime { get; init; }
    public int? DataFeedRunRetentionDays { get; init; }

    /// <summary>D-200: null = unchanged; "" = off; else "HH:mm".</summary>
    public string? ScheduledBackupTime { get; init; }

    /// <summary>D-200: null = unchanged.</summary>
    public int? ScheduledBackupRetentionDays { get; init; }

    public string? BusinessHoursStart { get; init; }
    public string? BusinessHoursEnd { get; init; }
    public string? BusinessDays { get; init; }

    /// <summary>D-182: null = unchanged.</summary>
    public int? BulkEditMaxAccounts { get; init; }

    /// <summary>D-220: null = unchanged.</summary>
    public int? ExportMaxRows { get; init; }

    /// <summary>D-243: null = unchanged.</summary>
    public int? ApiKeyDefaultLifetimeDays { get; init; }

    /// <summary>D-243: null = unchanged.</summary>
    public int? ApiRateLimitPerMinute { get; init; }

    /// <summary>D-186: each pattern's Mode and Value; a null Mode leaves that pattern unchanged.</summary>
    public string? SafeDecomMode { get; init; }
    public string? SafeDecomValue { get; init; }
    public string? AccountDecomMode { get; init; }
    public string? AccountDecomValue { get; init; }
    public string? SafeIgnoreMode { get; init; }
    public string? SafeIgnoreValue { get; init; }

    /// <summary>
    /// D-224: null = unchanged; "" = not set; else an absolute https URL
    /// (http only for localhost). Settable: the controller replaces it with
    /// the normalized form (SiteLinks.TryNormalize) before saving.
    /// </summary>
    public string? SiteUrl { get; set; }
}
