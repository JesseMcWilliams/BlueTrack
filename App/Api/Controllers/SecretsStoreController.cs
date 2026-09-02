using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/admin/secrets-store")]
[Authorize(Policy = Permissions.ManageSecretsStore)]
public sealed class SecretsStoreController(
    SecretsStoreRepository repository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
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
}
