namespace BlueTrack.Api.Models;

/// <summary>
/// Body for POST /api/risk-exceptions. Exactly one of AccountKey/
/// ApplicationKey must be set (D-18/Q-25) -- enforced in
/// RiskExceptionsController, not the database, consistent with how this
/// project avoids triggers/CHECK constraints for business rules elsewhere.
/// </summary>
public sealed class CreateRiskExceptionRequest
{
    public long? AccountKey { get; init; }
    public int? ApplicationKey { get; init; }
    public required string Justification { get; init; }
    public required DateTime ReviewDate { get; init; }
    public string? ExternalTicketReference { get; init; }
}
