using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>
/// D-230: appsettings.Site.json's place among WebApplication.CreateBuilder's
/// configuration sources, against a real builder over a temporary content root.
/// </summary>
public sealed class SiteSettingsTests : IDisposable
{
    private const string Key = "SiteSettingsProbe:Value";
    private const string EnvVarName = "SiteSettingsProbe__Value";

    private readonly string _contentRoot = Directory.CreateTempSubdirectory("BlueTrackSiteSettings_").FullName;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(EnvVarName, null);
        Directory.Delete(_contentRoot, recursive: true);
    }

    private void WriteJson(string fileName, string value) =>
        File.WriteAllText(Path.Combine(_contentRoot, fileName), $$"""{ "SiteSettingsProbe": { "Value": "{{value}}" } }""");

    private WebApplicationBuilder Builder(string environmentName, params string[] args)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = _contentRoot,
            EnvironmentName = environmentName,
            Args = args,
        });
        builder.Configuration.AddSiteSettingsFile(builder.Environment);
        return builder;
    }

    [Fact]
    public void SiteFile_IsPlacedAfterTheAppSettingsFiles_AndBeforeEnvironmentVariablesAndCommandLine()
    {
        // CreateBuilder adds the command-line source only when there are arguments.
        var sources = Builder(Environments.Production, "--SiteSettingsProbe:Unused=1").Configuration.Sources.ToList();
        var listing = string.Join(", ", sources.Select(s => s is FileConfigurationSource f ? f.Path : s.GetType().Name));

        int IndexOfJson(string path) => sources.FindIndex(s => s is JsonConfigurationSource j && j.Path == path);
        var site = IndexOfJson(SiteSettings.FileName);
        var appSettings = IndexOfJson("appsettings.json");
        var appSettingsProduction = IndexOfJson("appsettings.Production.json");
        // The unprefixed environment variables source: the one app settings come from.
        var environmentVariables = sources.FindLastIndex(s => s is EnvironmentVariablesConfigurationSource e && string.IsNullOrEmpty(e.Prefix));
        var commandLine = sources.FindLastIndex(s => s is CommandLineConfigurationSource);

        Assert.True(appSettings >= 0 && appSettingsProduction >= 0 && environmentVariables >= 0 && commandLine >= 0, listing);
        Assert.Equal(appSettingsProduction + 1, site);
        Assert.True(appSettings < site);
        Assert.True(site < environmentVariables);
        Assert.True(site < commandLine);
    }

    [Fact]
    public void SiteFile_OverridesAppSettingsJsonAndTheEnvironmentFile()
    {
        WriteJson("appsettings.json", "built-in");
        WriteJson("appsettings.Production.json", "environment-file");
        WriteJson(SiteSettings.FileName, "site");

        Assert.Equal("site", Builder(Environments.Production).Configuration[Key]);
    }

    [Fact]
    public void EnvironmentVariable_OverridesSiteFile()
    {
        WriteJson(SiteSettings.FileName, "site");
        Environment.SetEnvironmentVariable(EnvVarName, "environment-variable");

        Assert.Equal("environment-variable", Builder(Environments.Production).Configuration[Key]);
    }

    [Fact]
    public void CommandLine_OverridesSiteFile()
    {
        WriteJson(SiteSettings.FileName, "site");

        Assert.Equal("command-line", Builder(Environments.Production, $"--{Key}=command-line").Configuration[Key]);
    }

    [Fact]
    public void MissingSiteFile_IsIgnored()
    {
        WriteJson("appsettings.json", "built-in");

        Assert.Equal("built-in", Builder(Environments.Production).Configuration[Key]);
    }

    [Fact]
    public void Development_NeverLoadsTheSiteFile()
    {
        WriteJson("appsettings.json", "built-in");
        WriteJson(SiteSettings.FileName, "site");

        var builder = Builder(Environments.Development);

        Assert.Equal("built-in", builder.Configuration[Key]);
        Assert.DoesNotContain(builder.Configuration.Sources, s => s is JsonConfigurationSource { Path: SiteSettings.FileName });
    }

    [Theory]
    [InlineData("Test", "Test")]
    [InlineData(" Staging ", "Staging")]
    [InlineData("", "Production")]
    [InlineData(null, "Production")]
    public void DisplayEnvironmentName_PrefersTheInstallersAnswer(string? installerAnswer, string expected)
    {
        var builder = Builder(Environments.Production);
        builder.Configuration[SiteSettings.EnvironmentNameKey] = installerAnswer;

        Assert.Equal(expected, SiteSettings.DisplayEnvironmentName(builder.Configuration, builder.Environment));
    }
}
