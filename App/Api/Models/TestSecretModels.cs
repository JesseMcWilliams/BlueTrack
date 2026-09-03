namespace BlueTrack.Api.Models;

/// <summary>Body for POST /api/admin/secrets-store/test.</summary>
public sealed class TestSecretRequest
{
    public required string Safe { get; init; }
    public required string Folder { get; init; }
    public required string Object { get; init; }
}

/// <summary>
/// Never carries the actual secret value -- only non-secret identifying
/// metadata (D-39: UserName/Address) and enough to tell success from
/// failure, consistent with never surfacing a raw secret through a general
/// admin UI/API response.
/// </summary>
public sealed class TestSecretResult
{
    public bool Success { get; init; }
    public string? UserName { get; init; }
    public string? Address { get; init; }
    public int? PasswordLength { get; init; }
    public bool FromFallbackCache { get; init; }
    public string? Error { get; init; }
    public string? ErrorCategory { get; init; }
}
