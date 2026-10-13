using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BlueTrack.Api.Controllers;

/// <summary>
/// D-213: the user menu's About -- the application's version, for any
/// signed-in user (the Deployment page's fuller version and build details
/// stay behind ViewDeploymentInfo). The version is BlueTrack.Api.csproj's
/// &lt;Version&gt;, without the "+commit" the build appends.
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
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = informational?.Split('+')[0];
        return string.IsNullOrEmpty(version) ? assembly.GetName().Version?.ToString(3) ?? "unknown" : version;
    }
}
