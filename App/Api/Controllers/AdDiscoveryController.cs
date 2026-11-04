using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using BlueTrack.Api.AdDiscovery;
using BlueTrack.Api.Audit;
using BlueTrack.Api.Auth;
using BlueTrack.Api.DataFeeds;

namespace BlueTrack.Api.Controllers;

/// <summary>D-317: AD Account Discovery as a data source on Admin > Data Sources: its schedule, recent runs and Run now.</summary>
[ApiController]
[Route("api/admin/ad-discovery")]
[Authorize(Policy = Permissions.ManageDataSources)]
public sealed class AdDiscoveryController(
    AdDiscoveryRunRepository repository,
    AdDiscoveryRunner runner,
    CurrentUserResolver currentUserResolver,
    AuditLogger auditLogger) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get() => Ok(new
    {
        runTime = await repository.GetRunTimeAsync(),
        domains = await repository.GetDomainsAsync(),   // D-324
        isRunning = runner.IsRunning,
        recentRuns = await repository.GetRecentAsync(10)
    });

    public sealed class AdDiscoveryDomainRequest
    {
        public bool UseAsDataSource { get; init; }
    }

    /// <summary>D-324: whether discovery reads this LDAP domain. The domain itself is set up on Credentials & LDAP.</summary>
    [HttpPut("domains/{ldapConfigKey:int}")]
    public async Task<IActionResult> SetDomain(int ldapConfigKey, [FromBody] AdDiscoveryDomainRequest request,
        [FromServices] Data.LdapConfigRepository ldapConfigRepository)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        var before = await ldapConfigRepository.GetByKeyAsync(ldapConfigKey);
        if (before is null) return NotFound();
        await ldapConfigRepository.SetUseAsDataSourceAsync(ldapConfigKey, request.UseAsDataSource, user.UserKey);
        if (before.UseAsDataSource != request.UseAsDataSource)
        {
            await auditLogger.LogAsync("FieldEdit", user.UserKey, "ldap_config", ldapConfigKey.ToString(),
                fieldChanges: [new FieldChange("UseAsDataSource", before.UseAsDataSource.ToString(), request.UseAsDataSource.ToString())]);
        }
        return NoContent();
    }

    public sealed class AdDiscoverySettingsRequest
    {
        public string? RunTime { get; init; }
    }

    [HttpPut("settings")]
    public async Task<IActionResult> SaveSettings([FromBody] AdDiscoverySettingsRequest request)
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        if (!FeedSchedule.TryParseTime(request.RunTime, out var time))
        {
            return Problem(title: "Invalid run time", detail: "The run time must be HH:mm (24-hour, server time).", statusCode: StatusCodes.Status400BadRequest);
        }

        var before = await repository.GetRunTimeAsync();
        var runTime = time.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        await repository.SetRunTimeAsync(runTime);
        if (before != runTime)
        {
            await auditLogger.LogAsync("FieldEdit", user.UserKey, "app_config", entityKey: null, fieldChanges: [new FieldChange("AdDiscoveryRunTime", before, runTime)]);
        }
        return NoContent();
    }

    [HttpPost("run")]
    public async Task<IActionResult> RunNow()
    {
        var user = await currentUserResolver.ResolveAsync(User);
        if (user is null) return Unauthorized();
        return runner.TryStart(user.DisplayName ?? user.UserKey.ToString())
            ? Accepted()
            : Problem(title: "Already running", detail: "AD Account Discovery is already running.", statusCode: StatusCodes.Status409Conflict);
    }
}
