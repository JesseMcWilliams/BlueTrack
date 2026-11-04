namespace BlueTrack.Api.Models;

/// <summary>
/// D-334: one exception an application's owner is emailed about (RiskExceptionRepository.GetOwnerReviewsAsync).
/// Kind is "Overdue" (past its review date) or "DueSoon" (the reminder, OwnerReminderDays ahead).
/// </summary>
public sealed class OwnerReviewRow
{
    public required string OwnerEmail { get; init; }
    public string? OwnerName { get; init; }
    public required string ApplicationName { get; init; }
    public int ExceptionKey { get; init; }
    public required string ExceptionID { get; init; }
    public required string ScopeType { get; init; }
    public required string ScopeName { get; init; }
    public DateTime ReviewDate { get; init; }
    public required string Kind { get; init; }
}
