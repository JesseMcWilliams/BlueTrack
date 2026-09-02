namespace BlueTrack.Api.Models;

/// <summary>One row from web.account_progress_field_metadata (Design_Interface_Extensibility.md).</summary>
public sealed class FieldMetadataItem
{
    public int FieldMetadataKey { get; init; }
    public required string FieldName { get; init; }
    public required string DisplayLabel { get; init; }
    public required string FieldType { get; init; }
    public string? ReferenceTable { get; init; }
    public bool IsRequired { get; init; }
    public int? RequiredPermission { get; init; }
    public string? RequiredPermissionName { get; init; }
    public int DisplayOrder { get; init; }
}

public sealed class SaveFieldMetadataRequest
{
    public required string FieldName { get; init; }
    public required string DisplayLabel { get; init; }
    public required string FieldType { get; init; }
    public string? ReferenceTable { get; init; }
    public bool IsRequired { get; init; }
    public int? RequiredPermission { get; init; }
    public int DisplayOrder { get; init; }
}
