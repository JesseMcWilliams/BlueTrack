namespace BlueTrack.Api.Models;

/// <summary>
/// web.notification_config -- singleton SMTP settings row
/// (Design_Notifications.md, D-115). PasswordSecretReference is never
/// returned to the admin UI once set (see NotificationRepository.Redact);
/// HasPassword tells the UI whether one is already stored without
/// exposing it, same pattern as IdentityProviderDetail.SecretReference.
/// </summary>
public sealed class NotificationConfig
{
    public int NotificationConfigKey { get; init; }
    public string? SmtpHost { get; init; }
    public int SmtpPort { get; init; }
    public bool EnableStartTls { get; init; }
    public required string AuthMethod { get; init; }
    public string? Username { get; init; }
    public string? PasswordSecretReference { get; init; }
    public string? FromAddress { get; init; }
    public string? FromDisplayName { get; init; }
}

public sealed class SaveNotificationConfigRequest
{
    public string? SmtpHost { get; init; }
    public int SmtpPort { get; init; }
    public bool EnableStartTls { get; init; }
    public required string AuthMethod { get; init; }
    public string? Username { get; init; }
    public string? FromAddress { get; init; }
    public string? FromDisplayName { get; init; }

    /// <summary>Write-only, like IdentityProviderConfig's PlaintextSecret -- left blank keeps whatever password (if any) is already stored.</summary>
    public string? PlaintextPassword { get; init; }
}
