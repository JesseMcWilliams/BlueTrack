namespace BlueTrack.Api.Models;

/// <summary>
/// One row from dbo.vw_reconciliation_review_queue
/// (Database/03_BlueTrack_AccountReconciliation.sql) -- an unconfirmed
/// cross-source account match awaiting human review (D-56). Read-only here;
/// the confirm/reject actions the view's header comment describes aren't
/// wired up in this scaffold yet.
/// </summary>
public sealed class ReconciliationReviewItem
{
    public long ReconciliationKey { get; init; }
    public string? SelfHostedAccountId { get; init; }
    public string? SelfHostedAccountName { get; init; }
    public string? SelfHostedUserName { get; init; }
    public string? SelfHostedAddress { get; init; }
    public string? PrivCloudAccountId { get; init; }
    public string? PrivCloudAccountName { get; init; }
    public string? PrivCloudUserName { get; init; }
    public string? PrivCloudAddress { get; init; }
    public required string MatchMethod { get; init; }
    public required string MatchConfidence { get; init; }
    public DateTime MatchedDate { get; init; }
    public string? Notes { get; init; }
    public int ReviewPriority { get; init; }
}
