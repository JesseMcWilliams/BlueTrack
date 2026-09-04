namespace BlueTrack.Api.Models;

/// <summary>
/// One row from web.vw_account_application_exception (D-81) -- an Active
/// application-scoped exception that covers this account through its
/// Safe's Application, computed live rather than stored on
/// fact_account_progress.ExceptionKey (which only ever holds the
/// account-scoped pointer, D-77).
/// </summary>
public sealed class ApplicationScopedException
{
    public required string ExceptionID { get; init; }
    public int ApplicationKey { get; init; }
    public required string ApplicationName { get; init; }
    public DateTime ReviewDate { get; init; }
}
