namespace BlueTrack.Api.Models;

/// <summary>One row on the Audit Log Viewer (Design_Audit_Logging.md).</summary>
public sealed class AuditEventSummary
{
    public long AuditEventKey { get; init; }
    public required string EventTypeName { get; init; }
    public DateTime OccurredAt { get; init; }
    public string? PerformedByName { get; init; }
    public string? EntityName { get; init; }
    public string? EntityKey { get; init; }
    public string? SourceIpAddress { get; init; }
    public string? Detail { get; init; }
    public string? Reason { get; init; }
}
