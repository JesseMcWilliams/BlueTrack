namespace BlueTrack.Api.RiskScoring;

/// <summary>
/// D-122: thrown by AccessGroupRepository when a new/edited row's
/// (GroupName, GroupIdentifier, FoundOnTargetKey) combination matches
/// another existing row -- mirrors RiskScoreBandOverlapException's shape
/// (a sealed, purpose-built exception type the controller catches to turn
/// into a clean 409 Conflict), the established convention for signaling a
/// validation failure out of a repository back to its controller.
/// </summary>
public sealed class DuplicateAccessGroupException(string message) : Exception(message);
