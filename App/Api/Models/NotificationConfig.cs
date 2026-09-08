namespace BlueTrack.Api.Models;

/// <summary>
/// web.notification_config -- singleton SMTP settings row
/// (Design_Notifications.md, D-115). D-116 migrated the SMTP account off
/// its own inline Username/PasswordSecretReference columns onto a shared
/// SmtpCredentialKey -> web.credential row (the same store the new LDAP
/// bind account uses) -- see CredentialRepository/SmtpNotificationSender.
/// </summary>
public sealed class NotificationConfig
{
    public int NotificationConfigKey { get; init; }
    public string? SmtpHost { get; init; }
    public int SmtpPort { get; init; }
    public bool EnableStartTls { get; init; }
    public required string AuthMethod { get; init; }
    public int? SmtpCredentialKey { get; init; }
    public string? SmtpCredentialName { get; init; }
    public string? FromAddress { get; init; }
    public string? FromDisplayName { get; init; }
}

public sealed class SaveNotificationConfigRequest
{
    public string? SmtpHost { get; init; }
    public int SmtpPort { get; init; }
    public bool EnableStartTls { get; init; }
    public required string AuthMethod { get; init; }
    public int? SmtpCredentialKey { get; init; }
    public string? FromAddress { get; init; }
    public string? FromDisplayName { get; init; }
}
