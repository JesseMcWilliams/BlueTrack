using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-213: the user menu's About -- the application's version, for any
/// signed-in user (the Deployment page's fuller version and build details
/// stay behind ViewDeploymentInfo).
///
/// D-226: the version is the assembly's informational version, which the
/// build stamps from git (BlueTrack.Api.csproj, BlueTrackStampVersion): the
/// csproj &lt;Version&gt; on a release tag, "1.0.0-dev.12+abc1234" between
/// releases, plain &lt;Version&gt; without git. The Deployment page shows the
/// same string.
/// </summary>
[ApiController]
[Route("api/about")]
[Authorize]
public sealed class AboutController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { Version = ProductVersion() });

    public static string ProductVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        return ProductVersion(
            assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.GetName().Version);
    }

    /// <summary>The informational version when there is one, else the assembly version as x.y.z.</summary>
    public static string ProductVersion(string? informationalVersion, Version? assemblyVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
            return informationalVersion.Trim();
        if (assemblyVersion is null)
            return "unknown";
        return assemblyVersion.Build >= 0 ? assemblyVersion.ToString(3) : assemblyVersion.ToString();
    }
}
