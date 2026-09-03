using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.Secrets;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/admin/secrets-store")]
[Authorize(Policy = Permissions.ManageSecretsStore)]
public sealed class SecretsStoreController(
    SecretsStoreRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger,
    ISecretsProvider secretsProvider) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repository.GetAllAsync());

    [HttpPut("active")]
    public async Task<IActionResult> SetActive([FromBody] SetActiveSecretsStoreRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.SetActiveAsync(request);
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "secrets_store", entityKey: request.BackendType,
            detail: $"Active secrets store backend set to {request.BackendType}");
        return NoContent();
    }

    /// <summary>
    /// Attempts a real retrieval against the active backend (currently only
    /// CyberArkCP has an implementation) and reports success/failure --
    /// never the retrieved secret itself, only non-secret metadata (D-39).
    /// </summary>
    [HttpPost("test")]
    public async Task<IActionResult> TestConnection([FromBody] TestSecretRequest request)
    {
        try
        {
            var result = await secretsProvider.GetSecretAsync(new SecretQuery(request.Safe, request.Folder, request.Object));
            return Ok(new TestSecretResult
            {
                Success = true,
                UserName = result.UserName,
                Address = result.Address,
                PasswordLength = result.Content.Length,
                FromFallbackCache = result.FromFallbackCache
            });
        }
        catch (SecretRetrievalException ex)
        {
            return Ok(new TestSecretResult
            {
                Success = false,
                Error = ex.Message,
                ErrorCategory = ex.Category.ToString()
            });
        }
    }
}
