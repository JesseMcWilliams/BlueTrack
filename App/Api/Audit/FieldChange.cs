namespace BlueTrack.Api.Audit;

/// <summary>One row for web.audit_field_change -- see AuditLogger.</summary>
public sealed record FieldChange(string FieldName, string? OldValue, string? NewValue);
