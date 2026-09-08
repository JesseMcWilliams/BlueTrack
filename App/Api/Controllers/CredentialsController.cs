using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    CurrentUserResolver currentUserResolver) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repository.GetAllAsync());

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] SaveCredentialRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var key = await repository.CreateAsync(request, user.UserKey);
        return CreatedAtAction(nameof(GetAll), new { }, new { credentialKey = key });
    }

    [HttpPut("{credentialKey:int}")]
    public async Task<IActionResult> Update(int credentialKey, [FromBody] SaveCredentialRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.UpdateAsync(credentialKey, request, user.UserKey);
        return NoContent();
    }

    [HttpDelete("{credentialKey:int}")]
    public async Task<IActionResult> Delete(int credentialKey)
    {
        await repository.DeleteAsync(credentialKey);
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
    public async Task<IActionResult> GetLdapConfig() => Ok(await ldapConfigRepository.GetAsync());

    [HttpPut("ldap-config")]
    public async Task<IActionResult> SaveLdapConfig([FromBody] SaveLdapConfigRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await ldapConfigRepository.SaveAsync(request, user.UserKey);
        return NoContent();
    }
}
