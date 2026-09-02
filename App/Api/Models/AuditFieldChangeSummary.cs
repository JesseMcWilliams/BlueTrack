namespace BlueTrack.Api.Models;

/// <summary>One field-level diff row, drilled into from an AuditEventSummary.</summary>
public sealed class AuditFieldChangeSummary
{
    public required string FieldName { get; init; }
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }
}
