namespace BlueTrack.Api.Models;

/// <summary>Full web.dim_application row for the Application ↔ Safe Mapping admin page.</summary>
public sealed class ApplicationDetail
{
    public int ApplicationKey { get; init; }
    public Guid ApplicationGUID { get; init; }
    public required string ApplicationCode { get; init; }
    public required string ApplicationName { get; init; }
    public string? Description { get; init; }
    public string? OwnerName { get; init; }
    public string? OwnerEmail { get; init; }
    public string? TechnicalName { get; init; }
    public string? TechnicalEmail { get; init; }
    public string? Notes { get; init; }
}

public sealed class SaveApplicationRequest
{
    public required string ApplicationCode { get; init; }
    public required string ApplicationName { get; init; }
    public string? Description { get; init; }
    public string? OwnerName { get; init; }
    public string? OwnerEmail { get; init; }
    public string? TechnicalName { get; init; }
    public string? TechnicalEmail { get; init; }
    public string? Notes { get; init; }
}

/// <summary>One dbo.dim_safe row, for assigning it to an Application.</summary>
public sealed class SafeSummary
{
    public int SafeKey { get; init; }
    public required string SafeName { get; init; }
    public int? ApplicationKey { get; init; }
    public string? ApplicationName { get; init; }
}
