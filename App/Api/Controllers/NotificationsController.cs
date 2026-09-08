using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.Notifications;

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
    CurrentUserResolver currentUserResolver) : ControllerBase
{
    [HttpGet("config")]
    public async Task<IActionResult> GetConfig() => Ok(await repository.GetConfigAsync());

    [HttpPut("config")]
    public async Task<IActionResult> SaveConfig([FromBody] SaveNotificationConfigRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.SaveConfigAsync(request, user.UserKey);
        return NoContent();
    }

    [HttpGet("recipients")]
    public async Task<IActionResult> GetRecipients() => Ok(await repository.GetRecipientsAsync());

    [HttpPost("recipients")]
    public async Task<IActionResult> CreateRecipient([FromBody] SaveNotificationRecipientRequest request)
    {
        var key = await repository.CreateRecipientAsync(request);
        return CreatedAtAction(nameof(GetRecipients), new { }, new { recipientKey = key });
    }

    [HttpPut("recipients/{recipientKey:int}")]
    public async Task<IActionResult> UpdateRecipient(int recipientKey, [FromBody] SaveNotificationRecipientRequest request)
    {
        await repository.UpdateRecipientAsync(recipientKey, request);
        return NoContent();
    }

    [HttpDelete("recipients/{recipientKey:int}")]
    public async Task<IActionResult> DeleteRecipient(int recipientKey)
    {
        await repository.DeleteRecipientAsync(recipientKey);
        return NoContent();
    }

    /// <summary>D-116: optional per-alert-kind target role, additive to the flat recipient list above.</summary>
    [HttpGet("types")]
    public async Task<IActionResult> GetNotificationTypes() => Ok(await repository.GetNotificationTypesAsync());

    [HttpPut("types/{notificationTypeKey:int}/target-role")]
    public async Task<IActionResult> SetNotificationTypeTargetRole(int notificationTypeKey, [FromBody] SetNotificationTypeTargetRoleRequest request)
    {
        await repository.SetNotificationTypeTargetRoleAsync(notificationTypeKey, request.TargetRoleKey);
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
        var recipients = await repository.GetActiveRecipientEmailsAsync();
        if (recipients.Count == 0)
        {
            return Problem(title: "No active recipients", detail: "Add at least one active recipient before sending a test email.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        try
        {
            await sender.SendAsync(recipients, "BlueTrack: test notification",
                "This is a test message from BlueTrack's Notifications admin page. If you received this, SMTP is configured correctly.");
            return Ok(new { success = true, recipientCount = recipients.Count });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }
}
