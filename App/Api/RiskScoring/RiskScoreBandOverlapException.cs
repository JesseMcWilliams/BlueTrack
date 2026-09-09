namespace BlueTrack.Api.RiskScoring;

/// <summary>
/// D-120: thrown by RiskScoreBandRepository when a new/edited band's
/// [MinScore, MaxScore] range overlaps another existing band -- mirrors
/// Secrets/SecretRetrievalException.cs's shape (a sealed, purpose-built
/// exception type the controller catches to turn into a clean 400), the
/// established convention for signaling a validation failure out of a
/// repository/service back to its controller in this app.
/// </summary>
public sealed class RiskScoreBandOverlapException(string message) : Exception(message);
