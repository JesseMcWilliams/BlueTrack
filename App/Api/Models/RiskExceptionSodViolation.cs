namespace BlueTrack.Api.Models;

/// <summary>
/// One row in the Risk Exception segregation-of-duties report: an
/// ExceptionKey field-change event on fact_account_progress where the user
/// who made the change is the same user recorded as that exception's
/// ApprovedBy. Reported regardless of whether
/// web.app_config.EnforceRiskExceptionSegregationOfDuties is (or ever was)
/// on -- a detective control, not tied to the enforcement toggle.
/// </summary>
public sealed class RiskExceptionSodViolation
{
    public int ExceptionKey { get; init; }
    public required string ExceptionID { get; init; }
    public long AccountKey { get; init; }
    public required string AccountName { get; init; }
    public required string UserDisplayName { get; init; }
    public DateTime OccurredAt { get; init; }
}
