using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-230: the API as IIS runs it -- ASP.NET Core Production -- over a content
/// root holding an installer-style appsettings.Site.json. The database
/// connection still comes from the base factory (the file here holds no
/// connection string).
/// </summary>
public sealed class ProductionSiteFileFactory : BlueTrackWebApplicationFactory
{
    public const string InstallerEnvironment = "Staging";

    private readonly string _contentRoot = Directory.CreateTempSubdirectory("BlueTrackSiteFile_").FullName;

    public ProductionSiteFileFactory()
    {
        File.WriteAllText(Path.Combine(_contentRoot, SiteSettings.FileName), $$"""
            {
              "BlueTrack": { "EnvironmentName": "{{InstallerEnvironment}}" },
              "Security": { "EnableHsts": true },
              "FileAccess": { "AllowedRoots": [] }
            }
            """);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.UseEnvironment(Environments.Production);
        builder.UseContentRoot(_contentRoot);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(_contentRoot, recursive: true); } catch (IOException) { /* best effort */ }
    }
}

public class SiteSettingsProductionTests(ProductionSiteFileFactory factory) : IClassFixture<ProductionSiteFileFactory>
{
    private sealed record DeploymentInfo(string EnvironmentName);

    [Fact]
    public async Task DeploymentInfo_ShowsTheInstallersEnvironmentName()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");

        var info = await client.GetFromJsonAsync<DeploymentInfo>("/api/admin/deployment");

        Assert.Equal(ProductionSiteFileFactory.InstallerEnvironment, info!.EnvironmentName);
    }

    [Theory]
    [InlineData("/api/auth/dev/test-signin?username=TestUser.Admin")]
    [InlineData("/openapi/v1.json")]
    public async Task DevelopmentOnlyEndpoints_StayNotFound_WithTheSiteFilePresent(string url)
    {
        var client = factory.CreateClient(new() { AllowAutoRedirect = false });

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}

/// <summary>D-230: without a site file, the Deployment page falls back to ASP.NET Core's environment name.</summary>
public class DeploymentEnvironmentNameFallbackTests(BlueTrackWebApplicationFactory factory) : IClassFixture<BlueTrackWebApplicationFactory>
{
    private sealed record DeploymentInfo(string EnvironmentName);

    [Fact]
    public async Task DeploymentInfo_WithoutTheSetting_ShowsTheHostEnvironment()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");

        var info = await client.GetFromJsonAsync<DeploymentInfo>("/api/admin/deployment");

        Assert.Equal(Environments.Development, info!.EnvironmentName);
    }
}
