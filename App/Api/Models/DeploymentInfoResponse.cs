namespace BlueTrack.Api.Models;

/// <summary>
/// Backs the new Deployment admin page (Design_Admin_Deployment_Management.md,
/// D-96, Part 3): environment/version info, health checks, and SQL Server
/// backup status, all read-only.
/// </summary>
public sealed class DeploymentInfoResponse
{
    public required string EnvironmentName { get; init; }
    public required string Version { get; init; }
    public DateTime? BuildTimestampUtc { get; init; }
    public required IReadOnlyList<HealthCheckEntryResponse> HealthChecks { get; init; }
    public required BackupStatusResult BackupStatus { get; init; }
}

public sealed class HealthCheckEntryResponse
{
    public required string Name { get; init; }
    public required string Status { get; init; } // HealthStatus.ToString(): Healthy / Degraded / Unhealthy
    public string? Description { get; init; }
}

/// <summary>
/// D-97: this app's own SQL account is deliberately least-privileged and
/// very likely lacks read access to msdb until a DBA grants it -- Available
/// is false (with a plain-language Error, not an exception) when that read
/// fails, rather than surfacing a raw 500.
/// </summary>
public sealed class BackupStatusResult
{
    public required bool Available { get; init; }
    public string? Error { get; init; }
    public IReadOnlyList<BackupStatusEntry>? Entries { get; init; }
}

public sealed class BackupStatusEntry
{
    public required string BackupType { get; init; } // 'D' = full, 'I' = differential, 'L' = log (msdb.dbo.backupset.type)
    public DateTime? LastBackupFinishDate { get; init; }
}
