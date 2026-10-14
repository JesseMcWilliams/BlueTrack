using System.Security.Principal;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-216: the break-glass accounts on the Identity Providers page (they're
/// about sign-in, so the same ManageIdentityProviders permission). A Windows
/// account entered as DOMAIN\name (or .\name for a local account on this
/// server) is stored as its SID, which is what BlueTrack sees at sign-in; a
/// SID or an OIDC/SAML user identifier is stored as entered.
/// </summary>
[ApiController]
[Route("api/admin/break-glass-accounts")]
[Authorize(Policy = Permissions.ManageIdentityProviders)]
public sealed class BreakGlassAccountsController(
    BreakGlassAccountRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repository.GetAllAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveBreakGlassAccountRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var account = (request.Account ?? "").Trim();
        if (account.Length is 0 or > 300) return Invalid("Account is required, 300 characters at most.");
        if (request.Notes?.Length > 1000) return Invalid("Notes must be 1000 characters or fewer.");

        string identifier;
        try
        {
            identifier = ResolveIdentifier(account);
        }
        catch (Exception ex) when (ex is IdentityNotMappedException or ArgumentException)
        {
            return Invalid($"'{account}' isn't a Windows account this server can find. Enter DOMAIN\\name, .\\name for a local account, a SID, or an OIDC/SAML user identifier.");
        }

        if ((await repository.GetAllAsync()).Any(a => string.Equals(a.ExternalIdentifier, identifier, StringComparison.OrdinalIgnoreCase)))
        {
            return Problem(title: "Already a break-glass account", detail: $"'{account}' is already on the list.", statusCode: StatusCodes.Status409Conflict);
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? account : request.DisplayName.Trim();
        if (displayName.Length > 300) return Invalid("Display name must be 300 characters or fewer.");
        var key = await repository.CreateAsync(identifier, displayName, string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(), user.UserKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "break_glass_account", key.ToString(),
            detail: $"Break-glass account '{displayName}' ({identifier}) added");
        return Created($"/api/admin/break-glass-accounts", new { breakGlassAccountKey = key, externalIdentifier = identifier });
    }

    [HttpDelete("{breakGlassAccountKey:int}")]
    public async Task<IActionResult> Delete(int breakGlassAccountKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var removed = await repository.DeleteAsync(breakGlassAccountKey);
        if (removed is null) return NotFound();
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "break_glass_account", breakGlassAccountKey.ToString(),
            detail: $"Break-glass account '{removed}' removed");
        return NoContent();
    }

    /// <summary>DOMAIN\name or .\name becomes its SID; anything else (a SID, an OIDC/SAML identifier) is kept as entered.</summary>
    public static string ResolveIdentifier(string account)
    {
        if (!account.Contains('\\') || account.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) return account;
        var name = account.StartsWith(@".\", StringComparison.Ordinal) ? $@"{Environment.MachineName}\{account[2..]}" : account;
        return ((SecurityIdentifier)new NTAccount(name).Translate(typeof(SecurityIdentifier))).Value;
    }

    private ObjectResult Invalid(string detail) =>
        Problem(title: "Invalid break-glass account", detail: detail, statusCode: StatusCodes.Status400BadRequest);
}
