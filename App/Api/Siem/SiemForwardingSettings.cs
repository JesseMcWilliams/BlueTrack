using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace BlueTrack.Api.Siem;

/// <summary>D-234: web.siem_forwarding_config (one row), as the page and the sender use it.</summary>
public sealed class SiemForwardingSettings
{
    public bool IsEnabled { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; } = 6514;
    public string Transport { get; set; } = SiemTransports.Tls;
    public string Framing { get; set; } = SyslogFormatter.OctetCounting;
    public int Facility { get; set; } = 20;
    public string? SyslogHostName { get; set; }
    public string AppName { get; set; } = "BlueTrack";
    public int TimeoutSeconds { get; set; } = 10;
    public int MaxMessageBytes { get; set; } = 8192;
    public string TlsTrustMode { get; set; } = SiemTlsTrustModes.System;
    public string? TlsFingerprint { get; set; }
    public string? TlsCaCertificate { get; set; }
    public bool TlsCheckRevocation { get; set; } = true;
    public DateTime? ModifiedDate { get; set; }
    public string? ModifiedByName { get; set; }
}

public static class SiemTransports
{
    public const string Tcp = "Tcp";
    public const string Tls = "Tls";
    public static readonly IReadOnlyList<string> All = [Tcp, Tls];
}

public static class SiemTlsTrustModes
{
    /// <summary>The Windows trust store, with the host name checked.</summary>
    public const string System = "System";
    /// <summary>RFC 5425 4.2.2 end-entity matching: the collector's certificate has exactly this fingerprint.</summary>
    public const string Fingerprint = "Fingerprint";
    /// <summary>The given CA is the only trust anchor; the chain and host name are checked.</summary>
    public const string CaCertificate = "CaCertificate";
    public static readonly IReadOnlyList<string> All = [System, Fingerprint, CaCertificate];
}

/// <summary>D-234: per audit event type, forwarded or not and its CEF severity.</summary>
public sealed class SiemEventTypeSetting
{
    public int AuditEventTypeKey { get; set; }
    public string EventTypeName { get; set; } = "";
    public string? Description { get; set; }
    public bool IsForwarded { get; set; } = true;
    public int CefSeverity { get; set; } = 5;
}

public sealed class SaveSiemEventTypeSetting
{
    public int AuditEventTypeKey { get; set; }
    public bool IsForwarded { get; set; }
    public int CefSeverity { get; set; }
}

/// <summary>PUT /api/admin/siem-forwarding: the settings and, optionally, the per-type list.</summary>
public sealed class SaveSiemForwardingRequest
{
    public bool IsEnabled { get; set; }
    public string? Host { get; set; }
    public int Port { get; set; }
    public string? Transport { get; set; }
    public string? Framing { get; set; }
    public int Facility { get; set; } = 20;
    public string? SyslogHostName { get; set; }
    public string? AppName { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    public int MaxMessageBytes { get; set; } = 8192;
    public string? TlsTrustMode { get; set; }
    public string? TlsFingerprint { get; set; }
    public string? TlsCaCertificate { get; set; }
    public bool TlsCheckRevocation { get; set; } = true;
    public List<SaveSiemEventTypeSetting>? EventTypes { get; set; }
}

/// <summary>D-234: the forwarder's progress (web.siem_forwarding_state) plus what is waiting.</summary>
public sealed class SiemForwardingStatus
{
    public long? Watermark { get; set; }
    public DateTime? EnabledAt { get; set; }
    public long? EnabledFromAuditEventKey { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }
    public long? LastSentAuditEventKey { get; set; }
    public long EventsSent { get; set; }
    public string? LastError { get; set; }
    public DateTime? LastErrorAt { get; set; }
    public int ConsecutiveFailures { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTime? LeaseExpiresAt { get; set; }
    /// <summary>Rows of forwarded types above the watermark (committed or not).</summary>
    public int PendingCount { get; set; }
    public DateTime? OldestPendingAt { get; set; }
}

public static class SiemForwardingValidation
{
    private static readonly Regex HostPattern = new(@"^[A-Za-z0-9.\-:\[\]]+$", RegexOptions.CultureInvariant);

    /// <summary>Checks and normalises a save request; returns the errors (empty when valid) and the settings to store.</summary>
    public static (List<string> Errors, SiemForwardingSettings Settings) Validate(SaveSiemForwardingRequest request)
    {
        var errors = new List<string>();
        var transport = SiemTransports.All.FirstOrDefault(t => string.Equals(t, request.Transport?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (transport is null) errors.Add("Transport must be Tcp or Tls.");
        var framing = new[] { SyslogFormatter.OctetCounting, SyslogFormatter.LineFeed }
            .FirstOrDefault(f => string.Equals(f, request.Framing?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (framing is null) errors.Add("Framing must be OctetCounting or LineFeed.");
        var trustMode = SiemTlsTrustModes.All.FirstOrDefault(m => string.Equals(m, request.TlsTrustMode?.Trim() ?? SiemTlsTrustModes.System, StringComparison.OrdinalIgnoreCase));
        if (trustMode is null) errors.Add("TLS trust must be System, Fingerprint or CaCertificate.");

        var host = request.Host?.Trim();
        if (string.IsNullOrEmpty(host))
        {
            if (request.IsEnabled) errors.Add("Enter the collector's host name or IP address to turn forwarding on.");
            host = null;
        }
        else if (host.Length > 255 || !HostPattern.IsMatch(host))
        {
            errors.Add("Host must be a host name or IP address (letters, digits, dots, hyphens; an IPv6 address may use colons).");
        }

        if (request.Port is < 1 or > 65535) errors.Add("Port must be 1-65535.");
        if (request.Facility is < 0 or > 23) errors.Add("Facility must be 0-23.");
        if (request.TimeoutSeconds is < 1 or > 120) errors.Add("Timeout must be 1-120 seconds.");
        if (request.MaxMessageBytes is < 2048 or > 65535) errors.Add("Largest message must be 2048-65535 bytes.");

        var appName = string.IsNullOrWhiteSpace(request.AppName) ? "BlueTrack" : request.AppName.Trim();
        if (appName.Length > 48 || SyslogFormatter.HeaderField(appName, 48) != appName)
            errors.Add("App name must be 1-48 printable ASCII characters with no spaces.");
        var syslogHost = string.IsNullOrWhiteSpace(request.SyslogHostName) ? null : request.SyslogHostName.Trim();
        if (syslogHost is not null && (syslogHost.Length > 255 || SyslogFormatter.HeaderField(syslogHost, 255) != syslogHost))
            errors.Add("Syslog host name must be at most 255 printable ASCII characters with no spaces.");

        string? fingerprint = null;
        string? caCertificate = null;
        if (transport == SiemTransports.Tls && trustMode == SiemTlsTrustModes.Fingerprint)
        {
            fingerprint = NormalizeFingerprint(request.TlsFingerprint);
            if (fingerprint is null) errors.Add("The fingerprint must be the collector certificate's SHA-256 (64 hex digits) or SHA-1 (40 hex digits), with or without colons, optionally starting sha-256: or sha-1:.");
        }
        if (transport == SiemTransports.Tls && trustMode == SiemTlsTrustModes.CaCertificate)
        {
            caCertificate = request.TlsCaCertificate?.Trim();
            if (string.IsNullOrEmpty(caCertificate) || TryLoadCertificate(caCertificate) is null)
                errors.Add("Paste the CA certificate in PEM form (-----BEGIN CERTIFICATE----- ...).");
        }

        if (request.EventTypes is not null && request.EventTypes.Any(t => t.CefSeverity is < 0 or > 10))
            errors.Add("Severity must be 0-10.");

        var settings = new SiemForwardingSettings
        {
            IsEnabled = request.IsEnabled,
            Host = host,
            Port = request.Port,
            Transport = transport ?? SiemTransports.Tls,
            Framing = framing ?? SyslogFormatter.OctetCounting,
            Facility = request.Facility,
            SyslogHostName = syslogHost,
            AppName = appName,
            TimeoutSeconds = request.TimeoutSeconds,
            MaxMessageBytes = request.MaxMessageBytes,
            TlsTrustMode = trustMode ?? SiemTlsTrustModes.System,
            // Kept only for the mode that uses it, so a stale pin or CA can't come back to life unseen.
            TlsFingerprint = fingerprint,
            TlsCaCertificate = caCertificate,
            TlsCheckRevocation = request.TlsCheckRevocation
        };
        return (errors, settings);
    }

    /// <summary>
    /// "sha-256:AB:CD:..." / "AB CD ..." / "abcd..." to "SHA256:ABCD..." (or "SHA1:"), or null if it isn't one.
    /// RFC 5425 4.2.2 writes a fingerprint as colon-separated hex bytes after a hash label ("sha-1:").
    /// </summary>
    public static string? NormalizeFingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        string? label = null;
        foreach (var prefix in new[] { "sha-256:", "sha256:", "sha-1:", "sha1:" })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                label = prefix.Contains('2') ? "SHA256" : "SHA1";
                text = text[prefix.Length..];
                break;
            }
        }
        var hex = new string(text.Where(c => c is not (':' or ' ' or '-')).ToArray()).ToUpperInvariant();
        if (hex.Length == 0 || !hex.All(Uri.IsHexDigit)) return null;
        var byLength = hex.Length switch { 64 => "SHA256", 40 => "SHA1", _ => null };
        if (byLength is null || (label is not null && label != byLength)) return null;
        return $"{byLength}:{hex}";
    }

    /// <summary>The certificate's fingerprint in the stored form, for comparing with a pinned one.</summary>
    public static string Fingerprint(X509Certificate certificate, string algorithm)
    {
        var raw = certificate.GetRawCertData();
        var hash = algorithm == "SHA1" ? SHA1.HashData(raw) : SHA256.HashData(raw);
        return $"{algorithm}:{Convert.ToHexString(hash)}";
    }

    public static X509Certificate2? TryLoadCertificate(string pem)
    {
        try
        {
            return X509Certificate2.CreateFromPem(pem);
        }
        catch (CryptographicException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
