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

    /// <summary>D-116 follow-up: skips only the CRL/OCSP check (hostname/chain validation still enforced) -- found necessary against the real provisioned relay, now an admin-editable per-environment choice rather than hardcoded.</summary>
    public bool IgnoreCrlErrors { get; init; }

    /// <summary>D-116 follow-up: a full ServerCertificateValidationCallback bypass (hostname, chain, expiry -- everything). Broader and more dangerous than IgnoreCrlErrors; off by default.</summary>
    public bool IgnoreSslErrors { get; init; }
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
    public bool IgnoreCrlErrors { get; init; }
    public bool IgnoreSslErrors { get; init; }
}
