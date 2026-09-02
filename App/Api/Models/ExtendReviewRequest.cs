namespace BlueTrack.Api.Models;

/// <summary>Body for PUT /api/risk-exceptions/{key}/extend-review (re-approval, workflow step 4).</summary>
public sealed class ExtendReviewRequest
{
    public required DateTime NewReviewDate { get; init; }
}
