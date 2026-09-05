namespace BlueTrack.Api.Models;

/// <summary>Body for PUT /api/me/preferences/{key}.</summary>
public sealed class SetUserPreferenceRequest
{
    public required string Value { get; init; }
}
