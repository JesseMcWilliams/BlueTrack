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
    public required string BusinessHoursStart { get; init; }
    public required string BusinessHoursEnd { get; init; }
    public required string BusinessDays { get; init; }

    /// <summary>D-182: the most accounts one Account Progress bulk edit may change.</summary>
    public int BulkEditMaxAccounts { get; init; }
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
    public string? BusinessHoursStart { get; init; }
    public string? BusinessHoursEnd { get; init; }
    public string? BusinessDays { get; init; }

    /// <summary>D-182: null = unchanged.</summary>
    public int? BulkEditMaxAccounts { get; init; }
}
