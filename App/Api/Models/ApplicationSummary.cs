namespace BlueTrack.Api.Models;

/// <summary>
/// One row from web.dim_application, for the exception-scoping dropdown
/// and (later) the Application ↔ Safe Mapping admin page.
/// </summary>
public sealed class ApplicationSummary
{
    public int ApplicationKey { get; init; }
    public required string ApplicationCode { get; init; }
    public required string ApplicationName { get; init; }
}
