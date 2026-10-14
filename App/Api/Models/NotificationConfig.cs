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

    /// <summary>D-223: Microsoft365 -- the Entra tenant ID or domain name.</summary>
    public string? OAuthTenantId { get; init; }

    /// <summary>D-223: Microsoft365 and GenericOAuth2 -- the application (client) ID. Not a secret.</summary>
    public string? OAuthClientId { get; init; }

    /// <summary>D-223: Microsoft365 -- 'Secret' (the credential holds the client secret) or 'Certificate' (it holds a PEM certificate and private key).</summary>
    public string? OAuthClientCredentialType { get; init; }

    /// <summary>D-223: GenericOAuth2 -- the token endpoint (https).</summary>
    public string? OAuthTokenUrl { get; init; }

    /// <summary>D-223: GenericOAuth2 -- space-separated scopes (optional, RFC 6749 4.4.2).</summary>
    public string? OAuthScope { get; init; }

    /// <summary>D-223: GenericOAuth2 -- an optional 'audience' form value some providers require.</summary>
    public string? OAuthAudience { get; init; }

    /// <summary>D-223: GenericOAuth2 -- 'Basic' (Authorization header, RFC 6749 2.3.1) or 'Body' (client_id/client_secret form fields).</summary>
    public string? OAuthClientAuthStyle { get; init; }

    /// <summary>D-223: the mailbox to sign in as (XOAUTH2 user; Google's impersonated user). Blank means FromAddress.</summary>
    public string? OAuthUsername { get; init; }
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

    /// <summary>D-223: Microsoft365 -- the Entra tenant ID or domain name.</summary>
    public string? OAuthTenantId { get; init; }

    /// <summary>D-223: Microsoft365 and GenericOAuth2 -- the application (client) ID. Not a secret.</summary>
    public string? OAuthClientId { get; init; }

    /// <summary>D-223: Microsoft365 -- 'Secret' (the credential holds the client secret) or 'Certificate' (it holds a PEM certificate and private key).</summary>
    public string? OAuthClientCredentialType { get; init; }

    /// <summary>D-223: GenericOAuth2 -- the token endpoint (https).</summary>
    public string? OAuthTokenUrl { get; init; }

    /// <summary>D-223: GenericOAuth2 -- space-separated scopes (optional, RFC 6749 4.4.2).</summary>
    public string? OAuthScope { get; init; }

    /// <summary>D-223: GenericOAuth2 -- an optional 'audience' form value some providers require.</summary>
    public string? OAuthAudience { get; init; }

    /// <summary>D-223: GenericOAuth2 -- 'Basic' (Authorization header, RFC 6749 2.3.1) or 'Body' (client_id/client_secret form fields).</summary>
    public string? OAuthClientAuthStyle { get; init; }

    /// <summary>D-223: the mailbox to sign in as (XOAUTH2 user; Google's impersonated user). Blank means FromAddress.</summary>
    public string? OAuthUsername { get; init; }
}
