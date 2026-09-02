namespace BlueTrack.Api.Models;

/// <summary>One option for a field-metadata-driven Dropdown field (Design_Interface_Extensibility.md).</summary>
public sealed class DropdownOption
{
    public int Key { get; init; }
    public required string Name { get; init; }
}
