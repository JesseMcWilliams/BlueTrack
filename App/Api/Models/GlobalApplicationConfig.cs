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
    public int? RetentionDays { get; init; }
    public bool LogReadEvents { get; init; }
}

public sealed class SaveGlobalApplicationConfigRequest
{
    public int IdleTimeoutMinutes { get; init; }
    public required string BreadcrumbPosition { get; init; }
    public required string ExceptionIdPattern { get; init; }
    public int? RetentionDays { get; init; }
    public bool LogReadEvents { get; init; }
}
