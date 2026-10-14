using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Siem;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-234: Admin > SIEM Forwarding -- where the audit log is forwarded (syslog, CEF over TCP or TLS), which
/// event types and at what severity, the forwarder's progress, and a Test button. Gated by
/// ManageApplicationConfiguration, the permission that already governs the audit log's own settings
/// (retention, read events) on Global Application Configuration; ManageNotifications is about admin email
/// alerts, not where the audit trail goes. Saves are audited as FieldEdit, tests as ConnectionTest (D-210).
/// </summary>
[ApiController]
[Route("api/admin/siem-forwarding")]
[Authorize(Policy = Permissions.ManageApplicationConfiguration)]
public sealed class SiemForwardingController(
    SiemForwardingRepository repository,
    ISyslogTransport transport,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger,
    TimeProvider timeProvider) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get() => Ok(new
    {
        settings = await repository.GetSettingsAsync(),
        eventTypes = await repository.GetEventTypesAsync(),
        status = await repository.GetStatusAsync(),
        defaultHostName = SyslogFormatter.DefaultHostName(),
        deviceVersion = AboutController.ProductVersion()
    });

    [HttpPut]
    public async Task<IActionResult> Save([FromBody] SaveSiemForwardingRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var (errors, settings) = SiemForwardingValidation.Validate(request);
        var knownTypes = await repository.GetEventTypesAsync();
        if (request.EventTypes?.Any(t => knownTypes.All(k => k.AuditEventTypeKey != t.AuditEventTypeKey)) == true)
            errors.Add("Unknown audit event type.");
        if (errors.Count > 0)
        {
            return Problem(title: "Invalid SIEM forwarding settings", detail: string.Join(" ", errors), statusCode: StatusCodes.Status400BadRequest);
        }

        var before = await repository.GetSettingsAsync();
        await repository.SaveAsync(settings, request.EventTypes, user.UserKey);

        List<FieldChange> changes = [];
        void Track(string field, object? oldValue, object? newValue)
        {
            if (!Equals(oldValue, newValue)) changes.Add(new FieldChange(field, oldValue?.ToString(), newValue?.ToString()));
        }
        Track("IsEnabled", before.IsEnabled, settings.IsEnabled);
        Track("Host", before.Host, settings.Host);
        Track("Port", before.Port, settings.Port);
        Track("Transport", before.Transport, settings.Transport);
        Track("Framing", before.Framing, settings.Framing);
        Track("Facility", before.Facility, settings.Facility);
        Track("SyslogHostName", before.SyslogHostName, settings.SyslogHostName);
        Track("AppName", before.AppName, settings.AppName);
        Track("TimeoutSeconds", before.TimeoutSeconds, settings.TimeoutSeconds);
        Track("MaxMessageBytes", before.MaxMessageBytes, settings.MaxMessageBytes);
        Track("TlsTrustMode", before.TlsTrustMode, settings.TlsTrustMode);
        Track("TlsFingerprint", before.TlsFingerprint, settings.TlsFingerprint);
        // The CA as subject and SHA-256 fingerprint, not the whole PEM.
        Track("TlsCaCertificate", DescribeCa(before.TlsCaCertificate), DescribeCa(settings.TlsCaCertificate));
        Track("TlsCheckRevocation", before.TlsCheckRevocation, settings.TlsCheckRevocation);
        foreach (var type in request.EventTypes ?? [])
        {
            var old = knownTypes.First(k => k.AuditEventTypeKey == type.AuditEventTypeKey);
            Track($"{old.EventTypeName}.IsForwarded", old.IsForwarded, type.IsForwarded);
            Track($"{old.EventTypeName}.CefSeverity", old.CefSeverity, type.CefSeverity);
        }
        if (changes.Count > 0)
        {
            await auditLogger.LogAsync("FieldEdit", user.UserKey, "siem_forwarding_config", "1", detail: "SIEM forwarding settings changed", fieldChanges: changes);
        }
        return NoContent();
    }

    /// <summary>
    /// Sends one test CEF event with the saved settings and reports the outcome; audited as ConnectionTest.
    /// Works whether forwarding is on or off, so the destination can be checked before turning it on.
    /// </summary>
    [HttpPost("test")]
    public async Task<IActionResult> Test()
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var settings = await repository.GetSettingsAsync();
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            return Problem(title: "No collector", detail: "Save the collector's host first.", statusCode: StatusCodes.Status400BadRequest);
        }

        var target = $"{settings.Host}:{settings.Port} ({settings.Transport}, {settings.Framing})";
        var message = SiemForwarder.BuildTestMessage(settings, AboutController.ProductVersion(),
            settings.SyslogHostName ?? SyslogFormatter.DefaultHostName(), user.DisplayName ?? user.ExternalIdentifier, timeProvider.GetUtcNow().UtcDateTime);
        try
        {
            await transport.SendAsync(settings, [SyslogFormatter.Frame(message, settings.Framing)], HttpContext.RequestAborted);
            await auditLogger.LogAsync("ConnectionTest", user.UserKey, "siem_forwarding_config", "1",
                detail: AuditLogger.ClipDetail($"SIEM forwarding test event sent to {target}"));
            return Ok(new { success = true, target });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested)
        {
            var error = SiemForwarder.Describe(ex);
            await auditLogger.LogAsync("ConnectionTest", user.UserKey, "siem_forwarding_config", "1",
                detail: AuditLogger.ClipDetail($"SIEM forwarding test event to {target} failed: {error}"));
            return Ok(new { success = false, target, error });
        }
    }

    private static string? DescribeCa(string? pem)
    {
        if (string.IsNullOrWhiteSpace(pem)) return null;
        using var certificate = SiemForwardingValidation.TryLoadCertificate(pem);
        return certificate is null ? "(unreadable)" : $"{certificate.Subject} ({SiemForwardingValidation.Fingerprint(certificate, "SHA256")})";
    }
}
