using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-116: the new Credentials & LDAP admin page. Credential CRUD plus the
/// (singleton) LDAP configuration live under one controller/permission --
/// LDAP config only really has three fields of its own (domain controller,
/// search base, SSL) plus a picker for which Credential is the bind account,
/// small enough not to warrant a fully separate permission from the store
/// it directly depends on.
/// </summary>
[ApiController]
[Route("api/admin/credentials")]
[Authorize(Policy = Permissions.ManageCredentials)]
public sealed class CredentialsController(
    CredentialRepository repository,
    LdapConfigRepository ldapConfigRepository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    // D-198: every credential and LDAP change is audit-logged -- what changed,
    // never a password (only whether a new one was set).
    private static string Describe(SaveCredentialRequest request) =>
        $"'{request.CredentialName}' ({request.BackendType})"
        + (string.IsNullOrEmpty(request.PlaintextPassword) ? "" : ", new password set");

    private static string Describe(SaveLdapConfigRequest request) =>
        $"domain '{request.DomainName}', enabled={request.IsEnabled}, DC '{request.DomainController}', "
        + $"SSL={request.UseSsl}, trusted connection={request.UseTrustedConnection}, credential key {request.CredentialKey?.ToString() ?? "none"}";

    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repository.GetAllAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveCredentialRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var key = await repository.CreateAsync(request, user.UserKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "credential", key.ToString(), detail: $"Credential {Describe(request)} created");
        return CreatedAtAction(nameof(GetAll), new { }, new { credentialKey = key });
    }

    [HttpPut("{credentialKey:int}")]
    public async Task<IActionResult> Update(int credentialKey, [FromBody] SaveCredentialRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.UpdateAsync(credentialKey, request, user.UserKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "credential", credentialKey.ToString(), detail: $"Credential {Describe(request)} updated");
        return NoContent();
    }

    [HttpDelete("{credentialKey:int}")]
    public async Task<IActionResult> Delete(int credentialKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        await repository.DeleteAsync(credentialKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "credential", credentialKey.ToString(), detail: "Credential deleted");
        return NoContent();
    }

    /// <summary>Resolves the credential for real (decrypt/vault lookup) and reports success/failure -- never returns the password itself.</summary>
    [HttpPost("{credentialKey:int}/test")]
    public async Task<IActionResult> Test(int credentialKey)
    {
        try
        {
            var (username, _) = await repository.ResolveForUseAsync(credentialKey);
            return Ok(new { success = true, username });
        }
        catch (Exception ex)
        {
            return Ok(new { success = false, error = ex.Message });
        }
    }

    [HttpGet("ldap-config")]
    public async Task<IActionResult> GetAllLdapConfigs() => Ok(await ldapConfigRepository.GetAllAsync());

    [HttpPost("ldap-config")]
    public async Task<IActionResult> CreateLdapConfig([FromBody] SaveLdapConfigRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var key = await ldapConfigRepository.CreateAsync(request, user.UserKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "ldap_config", key.ToString(), detail: $"LDAP config created: {Describe(request)}");
        return CreatedAtAction(nameof(GetAllLdapConfigs), new { }, new { ldapConfigKey = key });
    }

    [HttpPut("ldap-config/{ldapConfigKey:int}")]
    public async Task<IActionResult> UpdateLdapConfig(int ldapConfigKey, [FromBody] SaveLdapConfigRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await ldapConfigRepository.UpdateAsync(ldapConfigKey, request, user.UserKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "ldap_config", ldapConfigKey.ToString(), detail: $"LDAP config updated: {Describe(request)}");
        return NoContent();
    }

    [HttpDelete("ldap-config/{ldapConfigKey:int}")]
    public async Task<IActionResult> DeleteLdapConfig(int ldapConfigKey)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        await ldapConfigRepository.DeleteAsync(ldapConfigKey);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "ldap_config", ldapConfigKey.ToString(), detail: "LDAP config deleted");
        return NoContent();
    }
}
