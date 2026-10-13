using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Controllers;

[ApiController]
[Route("api/admin/group-role-mappings")]
[Authorize(Policy = Permissions.ManageGroupRoleMapping)]
public sealed class GroupRoleMappingsController(
    GroupRoleMappingRepository repository,
    IdentityProviderRepository identityProviderRepository,
    AuthorizationRepository authorizationRepository,
    RoleRepository roleRepository,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll()
    {
        var mappings = await repository.GetAllAsync();
        foreach (var mapping in mappings)
        {
            if (mapping.ProviderType == "WindowsIntegrated")
            {
                mapping.GroupDisplayName = WindowsGroupResolver.TryGetAccountName(mapping.IdentityGroupName);
            }
        }
        return Ok(mappings);
    }

    /// <summary>
    /// Backs the Role dropdown on this page's own "Add Mapping" form --
    /// gated by ManageGroupRoleMapping (this controller's own policy)
    /// rather than ManageRolesAndPermissions (RolesController's), since an
    /// admin who can map groups to roles needs to see the role catalog to
    /// do that job even if they don't hold the separate permission to
    /// manage roles themselves.
    /// </summary>
    [HttpGet("roles")]
    public async Task<IActionResult> GetRoles() => Ok(await roleRepository.GetRolesAsync());

    /// <summary>
    /// D-204: the providers groups can be mapped for (the Add Mapping page's
    /// dropdown), with the claim OIDC/SAML groups arrive in. Gated by this
    /// controller's ManageGroupRoleMapping, like /roles, rather than
    /// ManageIdentityProviders.
    /// </summary>
    [HttpGet("providers")]
    public async Task<IActionResult> GetProviders()
    {
        var providers = (await identityProviderRepository.GetAllConfigsAsync())
            .Where(p => p.ProviderType != "DevFakeAuth")
            .Select(p => new MappableProvider
            {
                ProviderKey = p.ProviderKey,
                ProviderType = p.ProviderType,
                DisplayName = p.DisplayName,
                IsEnabled = p.IsEnabled,
                GroupClaimType = p.ProviderType is "OIDC" or "SAML" ? ProviderSettingsReader.ReadGroupClaimType(p) : null
            });
        return Ok(providers);
    }

    /// <summary>
    /// D-204: the provider a request names (null = WindowsIntegrated, as
    /// before) and the identifier to store and match for its group: a SID for
    /// WindowsIntegrated (D-69); for OIDC/SAML the group claim's value as the
    /// IdP sends it, which is what sign-in matches (GroupIdentifierExtractor).
    /// </summary>
    private async Task<(IdentityProviderConfig? Provider, string Identifier, string DisplayName, IActionResult? Error)> ResolveForProviderAsync(
        string groupName, int? providerKey, int groupNotFoundStatus)
    {
        var provider = providerKey is null
            ? await identityProviderRepository.GetByTypeAsync("WindowsIntegrated")
            : await identityProviderRepository.GetByKeyAsync(providerKey.Value);
        if (provider is null)
        {
            return (null, "", "", providerKey is null
                ? Problem(title: "No WindowsIntegrated provider configured", statusCode: StatusCodes.Status503ServiceUnavailable)
                : Problem(title: "Provider not found", detail: $"No identity provider {providerKey}.", statusCode: StatusCodes.Status400BadRequest));
        }

        if (provider.ProviderType == "WindowsIntegrated")
        {
            var resolved = WindowsGroupResolver.TryResolve(groupName);
            if (resolved is null)
            {
                // D-194: 400 on create, not 404 -- the URL is fine; the group named in the body is the problem.
                return (provider, "", "", Problem(title: "Group not found", detail: $"Could not resolve '{groupName}' to a Windows account.", statusCode: groupNotFoundStatus));
            }
            return (provider, resolved.Value.Sid, resolved.Value.ResolvedAccountName, null);
        }

        var claimValue = groupName.Trim();
        if (claimValue.Length is 0 or > 300)
        {
            return (provider, "", "", Problem(title: "Invalid group name", detail: "Enter the group's name as the identity provider sends it (1 to 300 characters).", statusCode: StatusCodes.Status400BadRequest));
        }
        return (provider, claimValue, claimValue, null);
    }

    /// <summary>
    /// The lookup/test tool (Design_Authorization-Model.md): resolves a
    /// friendly group name to its SID and shows what role(s)/permissions
    /// it currently resolves to, without saving anything.
    /// </summary>
    [HttpPost("resolve-group")]
    public async Task<IActionResult> ResolveGroup([FromBody] ResolveGroupRequest request)
    {
        var (provider, sid, resolvedAccountName, error) = await ResolveForProviderAsync(request.GroupName, request.ProviderKey, StatusCodes.Status404NotFound);
        if (error is not null) return error;

        var roleNames = await authorizationRepository.GetMatchedRoleNamesAsync(provider!.ProviderKey, [sid]);
        var permissionNames = await authorizationRepository.GetEffectivePermissionNamesAsync(provider.ProviderKey, [sid]);

        return Ok(new ResolveGroupResult
        {
            ResolvedAccountName = resolvedAccountName,
            Sid = sid,
            CurrentRoleNames = roleNames,
            CurrentPermissionNames = permissionNames
        });
    }

    /// <summary>
    /// D-107/Archive_Planning_Outstanding-Work-Survey-2026-09-16.md: generates a filled-in copy of
    /// Database/Manual/03_BlueTrack_GrantBackupStatusReaderRole.sql, targeting
    /// whichever group the admin just resolved, for a DBA to run manually
    /// via sqlcmd (Option B -- never through App/Migrator, see that
    /// script's own header). Same "resolve, then act" shape as
    /// resolve-group above -- reuses WindowsGroupResolver directly rather
    /// than requiring a caller to resolve first and pass the result in.
    /// </summary>
    [HttpPost("generate-backupstatus-reader-script")]
    public IActionResult GenerateBackupStatusReaderScript([FromBody] ResolveGroupRequest request)
    {
        var resolved = WindowsGroupResolver.TryResolve(request.GroupName);
        if (resolved is null)
        {
            return Problem(title: "Group not found", detail: $"Could not resolve '{request.GroupName}' to a Windows account.", statusCode: StatusCodes.Status404NotFound);
        }

        var (_, resolvedAccountName) = resolved.Value;
        var script = BackupStatusReaderScriptGenerator.Generate(resolvedAccountName);

        var safeFileNamePart = string.Concat(resolvedAccountName.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        return File(System.Text.Encoding.UTF8.GetBytes(script), "text/plain", $"Grant-db_backupstatus_reader-{safeFileNamePart}.sql");
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateGroupRoleMappingRequest request, [FromServices] UserRightsCache rightsCache)
    {
        var (provider, sid, resolvedAccountName, error) = await ResolveForProviderAsync(request.GroupName, request.ProviderKey, StatusCodes.Status400BadRequest);
        if (error is not null) return error;

        var roleKey = await repository.GetRoleKeyByNameAsync(request.RoleName);
        if (roleKey is null)
        {
            return Problem(title: "Role not found", detail: $"No role named '{request.RoleName}'.", statusCode: StatusCodes.Status400BadRequest);
        }

        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        var mappingKey = await repository.CreateAsync(provider!.ProviderKey, sid, roleKey.Value);
        await rightsCache.InvalidateAllAsync();   // D-197: applies to everyone's next request

        await auditLogger.LogAsync("FieldEdit", user.UserKey, "identity_group_role_map", mappingKey.ToString(),
            detail: provider.ProviderType == "WindowsIntegrated"
                ? $"Mapped {resolvedAccountName} ({sid}) to role '{request.RoleName}'"
                : $"Mapped {provider.ProviderType} group '{sid}' ({provider.DisplayName}) to role '{request.RoleName}'");

        return CreatedAtAction(nameof(GetAll), new { }, new { mappingKey, sid, resolvedAccountName });
    }

    [HttpDelete("{mappingKey:int}")]
    public async Task<IActionResult> Delete(int mappingKey, [FromServices] UserRightsCache rightsCache)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();

        await repository.DeleteAsync(mappingKey);
        await rightsCache.InvalidateAllAsync();   // D-197: applies to everyone's next request
        await auditLogger.LogAsync("FieldEdit", user.UserKey, "identity_group_role_map", mappingKey.ToString(), detail: "Mapping deleted");
        return NoContent();
    }
}
