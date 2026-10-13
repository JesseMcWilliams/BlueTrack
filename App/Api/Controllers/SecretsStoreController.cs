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
    VaultSecretProviderResolver vaultSecretProviderResolver,
    SecretFallbackCache fallbackCache) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll() => Ok(await repository.GetAllAsync());

    [HttpPut("active")]
    public async Task<IActionResult> SetActive([FromBody] SetActiveSecretsStoreRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        // D-202: backend URLs must be https.
        if (SecretsBackendUrls.Validate(request.BackendType, request.BackendSettings) is { } urlError)
        {
            return Problem(title: "Invalid backend URL", detail: urlError, statusCode: StatusCodes.Status400BadRequest);
        }

        await repository.SetActiveAsync(request);
        fallbackCache.Clear(); // D-202: nothing kept from the previous settings
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "secrets_store", entityKey: request.BackendType,
            detail: $"Active secrets store backend set to {request.BackendType}");
        return NoContent();
    }

    /// <summary>
    /// Attempts a real retrieval against whichever backend is currently
    /// active (VaultSecretProviderResolver) and reports success/failure --
    /// never the retrieved secret itself, only non-secret metadata (D-39).
    /// Only meaningful for vault-lookup backends (CyberArk CP/CCP/Conjur,
    /// Azure Key Vault, AWS Secrets Manager) -- Windows DPAPI doesn't fit
    /// this shape at all (D-79), so activating it makes this endpoint
    /// correctly fail with "no provider implementation."
    /// D-210: every test is audit-logged (ConnectionTest) with what was asked
    /// and the outcome; the password's length is no longer returned.
    /// </summary>
    [HttpPost("test")]
    public async Task<IActionResult> TestConnection([FromBody] TestSecretRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var asked = $"Test lookup of Safe '{request.Safe}', Folder '{request.Folder}', Object '{request.Object}'";
        TestSecretResult outcome;
        try
        {
            var secretsProvider = await vaultSecretProviderResolver.ResolveActiveAsync();
            var result = await secretsProvider.GetSecretAsync(new SecretQuery(request.Safe, request.Folder, request.Object));
            outcome = new TestSecretResult
            {
                Success = true,
                UserName = result.UserName,
                Address = result.Address,
                FromFallbackCache = result.FromFallbackCache
            };
            asked += result.FromFallbackCache ? ": succeeded (from the fallback cache)" : ": succeeded";
        }
        catch (SecretRetrievalException ex)
        {
            outcome = new TestSecretResult
            {
                Success = false,
                Error = ex.Message,
                ErrorCategory = ex.Category.ToString()
            };
            asked += $": failed ({ex.Category}): {ex.Message}";
        }

        await auditLogger.LogAsync("ConnectionTest", user.UserKey, "secrets_store", detail: AuditLogger.ClipDetail(asked));
        return Ok(outcome);
    }
}
