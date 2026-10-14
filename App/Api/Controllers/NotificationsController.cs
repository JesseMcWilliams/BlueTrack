using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.Notifications;
using BlueTrack.Api.Notifications.OAuth;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// Design_Notifications.md, D-115: SMTP settings, the recipient list, and
/// a "send test email" action for the Notifications admin page.
/// </summary>
[ApiController]
[Route("api/admin/notifications")]
[Authorize(Policy = Permissions.ManageNotifications)]
public sealed class NotificationsController(
    NotificationRepository repository,
    INotificationSender sender,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    [HttpGet("config")]
    public async Task<IActionResult> GetConfig() => Ok(await repository.GetConfigAsync());

    [HttpPut("config")]
    public async Task<IActionResult> SaveConfig([FromBody] SaveNotificationConfigRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        // D-223: the auth mode and each OAuth2 mode's own required fields.
        var errors = SmtpAuthMethods.Validate(request);
        if (errors.Count > 0)
        {
            return Problem(title: "Invalid SMTP settings", detail: string.Join(" ", errors), statusCode: StatusCodes.Status400BadRequest);
        }

        // D-198: a stored credential's password is only ever sent over TLS.
        var isOAuth = SmtpAuthMethods.IsOAuth(request.AuthMethod);
        var sendsCredential = (request.AuthMethod == SmtpAuthMethods.Basic || isOAuth) && request.SmtpCredentialKey is not null;
        if (sendsCredential && !request.EnableStartTls)
        {
            return Problem(title: "STARTTLS required", statusCode: StatusCodes.Status400BadRequest,
                detail: "Basic authentication sends the credential's password to the SMTP server, so STARTTLS must be on.");
        }

        // D-198: pointing a stored credential at a new destination is a
        // credential decision, not just a notification setting -- otherwise
        // ManageNotifications alone could send any stored password (the LDAP
        // bind account's, say) to a server of the user's choosing.
        var before = await repository.GetConfigAsync();
        var destinationChanged = before is null
            || !string.Equals(before.SmtpHost, request.SmtpHost, StringComparison.OrdinalIgnoreCase)
            || before.SmtpPort != request.SmtpPort
            || before.EnableStartTls != request.EnableStartTls
            || before.IgnoreSslErrors != request.IgnoreSslErrors
            || before.AuthMethod != request.AuthMethod
            || before.SmtpCredentialKey != request.SmtpCredentialKey
            // D-223: where an OAuth2 secret is posted (the tenant's or the
            // generic token endpoint), what token it buys, and whose mailbox
            // it signs in as (Google impersonates it) are destinations too.
            || !string.Equals(before.OAuthTenantId, request.OAuthTenantId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(before.OAuthClientId, request.OAuthClientId, StringComparison.Ordinal)
            || !string.Equals(before.OAuthTokenUrl, request.OAuthTokenUrl, StringComparison.Ordinal)
            || !string.Equals(before.OAuthScope, request.OAuthScope, StringComparison.Ordinal)
            || !string.Equals(before.OAuthAudience, request.OAuthAudience, StringComparison.Ordinal)
            || !string.Equals(before.OAuthUsername, request.OAuthUsername, StringComparison.OrdinalIgnoreCase)
            || (isOAuth && !string.Equals(before.FromAddress, request.FromAddress, StringComparison.OrdinalIgnoreCase));
        if (sendsCredential && destinationChanged
            && !User.HasClaim(PermissionClaimsTransformation.PermissionClaimType, Permissions.ManageCredentials))
        {
            return Problem(title: "Credential permission needed", statusCode: StatusCodes.Status403Forbidden,
                detail: "Sending a stored credential to this SMTP server also needs the ManageCredentials permission.");
        }

        await repository.SaveConfigAsync(request, user.UserKey);

        List<FieldChange> changes = [];
        void Track(string field, object? oldValue, object? newValue)
        {
            if (!Equals(oldValue, newValue)) changes.Add(new FieldChange(field, oldValue?.ToString(), newValue?.ToString()));
        }
        Track("SmtpHost", before?.SmtpHost, request.SmtpHost);
        Track("SmtpPort", before?.SmtpPort, request.SmtpPort);
        Track("EnableStartTls", before?.EnableStartTls, request.EnableStartTls);
        Track("AuthMethod", before?.AuthMethod, request.AuthMethod);
        Track("SmtpCredentialKey", before?.SmtpCredentialKey, request.SmtpCredentialKey);
        Track("FromAddress", before?.FromAddress, request.FromAddress);
        Track("FromDisplayName", before?.FromDisplayName, request.FromDisplayName);
        Track("IgnoreCrlErrors", before?.IgnoreCrlErrors, request.IgnoreCrlErrors);
        Track("IgnoreSslErrors", before?.IgnoreSslErrors, request.IgnoreSslErrors);
        // D-223: none of these is a secret (the secret stays in the credential).
        Track("OAuthTenantId", before?.OAuthTenantId, request.OAuthTenantId);
        Track("OAuthClientId", before?.OAuthClientId, request.OAuthClientId);
        Track("OAuthClientCredentialType", before?.OAuthClientCredentialType, request.OAuthClientCredentialType);
        Track("OAuthTokenUrl", before?.OAuthTokenUrl, request.OAuthTokenUrl);
        Track("OAuthScope", before?.OAuthScope, request.OAuthScope);
        Track("OAuthAudience", before?.OAuthAudience, request.OAuthAudience);
        Track("OAuthClientAuthStyle", before?.OAuthClientAuthStyle, request.OAuthClientAuthStyle);
        Track("OAuthUsername", before?.OAuthUsername, request.OAuthUsername);
        if (changes.Count > 0)
        {
            await auditLogger.LogAsync("FieldEdit", user.UserKey, "notification_config", before?.NotificationConfigKey.ToString(),
                detail: "SMTP settings changed", fieldChanges: changes);
        }
        return NoContent();
    }

    [HttpGet("recipients")]
    public async Task<IActionResult> GetRecipients() => Ok(await repository.GetRecipientsAsync());

    [HttpPost("recipients")]
    public async Task<IActionResult> CreateRecipient([FromBody] SaveNotificationRecipientRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        var key = await repository.CreateRecipientAsync(request);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "notification_recipient", key.ToString(), detail: $"Recipient '{request.Email}' added");   // D-198
        return CreatedAtAction(nameof(GetRecipients), new { }, new { recipientKey = key });
    }

    [HttpPut("recipients/{recipientKey:int}")]
    public async Task<IActionResult> UpdateRecipient(int recipientKey, [FromBody] SaveNotificationRecipientRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        await repository.UpdateRecipientAsync(recipientKey, request);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "notification_recipient", recipientKey.ToString(), detail: $"Recipient '{request.Email}' updated");   // D-198
        return NoContent();
    }

    [HttpDelete("recipients/{recipientKey:int}")]
    public async Task<IActionResult> DeleteRecipient(int recipientKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        await repository.DeleteRecipientAsync(recipientKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "notification_recipient", recipientKey.ToString(), detail: "Recipient deleted");   // D-198
        return NoContent();
    }

    /// <summary>D-116: optional per-alert-kind target role, additive to the flat recipient list above.</summary>
    [HttpGet("types")]
    public async Task<IActionResult> GetNotificationTypes() => Ok(await repository.GetNotificationTypesAsync());

    [HttpPut("types/{notificationTypeKey:int}/target-role")]
    public async Task<IActionResult> SetNotificationTypeTargetRole(int notificationTypeKey, [FromBody] SetNotificationTypeTargetRoleRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        await repository.SetNotificationTypeTargetRoleAsync(notificationTypeKey, request.TargetRoleKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_notification_type", notificationTypeKey.ToString(),
            detail: $"Target role set to {request.TargetRoleKey?.ToString() ?? "none"}");   // D-198
        return NoContent();
    }

    /// <summary>D-222: turns a notification type on or off; a disabled type is never evaluated or sent.</summary>
    [HttpPut("types/{notificationTypeKey:int}/enabled")]
    public async Task<IActionResult> SetNotificationTypeEnabled(int notificationTypeKey, [FromBody] SetNotificationTypeEnabledRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        var type = (await repository.GetNotificationTypesAsync()).FirstOrDefault(t => t.NotificationTypeKey == notificationTypeKey);
        if (type is null) return NotFound();
        await repository.SetNotificationTypeEnabledAsync(notificationTypeKey, request.IsEnabled);
        if (type.IsEnabled != request.IsEnabled)
        {
            await auditLogger.LogAsync("FieldEdit", user.UserKey, "dim_notification_type", notificationTypeKey.ToString(),
                detail: $"{type.NotificationTypeName} {(request.IsEnabled ? "enabled" : "disabled")}",
                fieldChanges: [new FieldChange("IsEnabled", type.IsEnabled.ToString(), request.IsEnabled.ToString())]);
        }
        return NoContent();
    }

    /// <summary>D-222: when the daily digests and the weekly summary go out (server local time).</summary>
    [HttpGet("schedule")]
    public async Task<IActionResult> GetSchedule() => Ok(await repository.GetScheduleAsync());

    [HttpPut("schedule")]
    public async Task<IActionResult> SaveSchedule([FromBody] NotificationSchedule request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        var errors = DigestSchedule.Validate(request);
        if (errors.Count > 0)
        {
            return Problem(title: "Invalid schedule", detail: string.Join(" ", errors), statusCode: StatusCodes.Status400BadRequest);
        }

        var before = await repository.GetScheduleAsync();
        await repository.SaveScheduleAsync(request, user.UserKey);

        List<FieldChange> changes = [];
        if (before.DailyDigestTime != request.DailyDigestTime) changes.Add(new FieldChange("DailyDigestTime", before.DailyDigestTime, request.DailyDigestTime));
        if (before.WeeklySummaryDay != request.WeeklySummaryDay) changes.Add(new FieldChange("WeeklySummaryDay", before.WeeklySummaryDay, request.WeeklySummaryDay));
        if (before.WeeklySummaryTime != request.WeeklySummaryTime) changes.Add(new FieldChange("WeeklySummaryTime", before.WeeklySummaryTime, request.WeeklySummaryTime));
        if (changes.Count > 0)
        {
            await auditLogger.LogAsync("FieldEdit", user.UserKey, "notification_config", null,
                detail: "Notification schedule changed", fieldChanges: changes);
        }
        return NoContent();
    }

    /// <summary>
    /// Sends a real test email to every active recipient using the
    /// currently-saved config -- lets an admin verify SMTP settings work
    /// before relying on the background check, same "test before trust"
    /// pattern as Secrets Store Configuration's own Test Connection.
    /// </summary>
    [HttpPost("test")]
    public async Task<IActionResult> SendTestEmail()
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        var recipients = await repository.GetActiveRecipientEmailsAsync();
        if (recipients.Count == 0)
        {
            return Problem(title: "No active recipients", detail: "Add at least one active recipient before sending a test email.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        // D-223: like D-210's Test buttons, the test send resolves the stored
        // credential (and, for OAuth2, gets a token) for real, so it is
        // audit-logged as a ConnectionTest. Messages never hold a secret.
        var authMethod = (await repository.GetConfigAsync()).AuthMethod;
        try
        {
            await sender.SendAsync(recipients, "BlueTrack: test notification",
                "This is a test message from BlueTrack's Notifications admin page. If you received this, SMTP is configured correctly.");
            await auditLogger.LogAsync("ConnectionTest", user.UserKey, "notification_config", null,
                detail: AuditLogger.ClipDetail($"Test email ({authMethod}) sent to {recipients.Count} recipient(s)"));
            return Ok(new { success = true, recipientCount = recipients.Count });
        }
        catch (Exception ex)
        {
            await auditLogger.LogAsync("ConnectionTest", user.UserKey, "notification_config", null,
                detail: AuditLogger.ClipDetail($"Test email ({authMethod}) failed: {ex.Message}"));
            return Ok(new { success = false, error = ex.Message });
        }
    }
}
