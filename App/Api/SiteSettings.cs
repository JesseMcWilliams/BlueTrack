using Microsoft.Extensions.Configuration.Json;

namespace BlueTrack.Api;

/// <summary>
/// D-230: the installer's per-site settings file, appsettings.Site.json, next to
/// the published API (Deploy/Modules/BlueTrack.Database.psm1,
/// Set-BlueTrackSiteConfig). IIS always runs the API as ASP.NET Core
/// Production, so the file isn't keyed to an environment name: the installer's
/// Environment answer is carried inside it as BlueTrack:EnvironmentName instead.
/// </summary>
public static class SiteSettings
{
    public const string FileName = "appsettings.Site.json";

    /// <summary>The installer's Environment answer (Development/Test/Staging/Production).</summary>
    public const string EnvironmentNameKey = "BlueTrack:EnvironmentName";

    /// <summary>
    /// Adds appsettings.Site.json (optional, not watched) right after
    /// appsettings.json / appsettings.{Environment}.json, so it overrides them
    /// while environment variables and command-line arguments, which
    /// WebApplication.CreateBuilder adds after those files, still override it.
    /// Not added in Development: dotnet run, the tests and E2E all run as
    /// Development, and a stray copy of a site's file must never point them at
    /// that site's database.
    /// </summary>
    public static void AddSiteSettingsFile(this IConfigurationBuilder configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            return;
        }

        var appSettingsFiles = new[] { "appsettings.json", $"appsettings.{environment.EnvironmentName}.json" };
        var lastAppSettingsIndex = -1;
        for (var i = 0; i < configuration.Sources.Count; i++)
        {
            if (configuration.Sources[i] is JsonConfigurationSource { Path: { } path }
                && appSettingsFiles.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                lastAppSettingsIndex = i;
            }
        }

        if (lastAppSettingsIndex < 0)
        {
            // Appending instead would put the file after environment variables
            // and command-line arguments, letting it override them.
            throw new InvalidOperationException(
                $"Could not find the appsettings.json configuration source to place {FileName} after.");
        }

        configuration.Sources.Insert(lastAppSettingsIndex + 1, new JsonConfigurationSource
        {
            FileProvider = configuration.GetFileProvider(),
            Path = FileName,
            Optional = true,
            ReloadOnChange = false,
        });
    }

    /// <summary>
    /// The environment name the Deployment page and health check show: the
    /// installer's answer when the site file gives one, else ASP.NET Core's own.
    /// </summary>
    public static string DisplayEnvironmentName(IConfiguration configuration, IHostEnvironment environment) =>
        configuration[EnvironmentNameKey] is { } name && !string.IsNullOrWhiteSpace(name)
            ? name.Trim()
            : environment.EnvironmentName;
}
