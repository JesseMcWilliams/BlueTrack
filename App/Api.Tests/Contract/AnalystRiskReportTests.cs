using System.Net;
using BlueTrack.Api.Tests.Integration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>D-208 (script 10): the Analyst role sees the Risk Score report; a Viewer still doesn't.</summary>
public class AnalystRiskReportTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public AnalystRiskReportTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private Task<HttpResponseMessage> GetAs(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client.GetAsync("/api/reports/risk-score");
    }

    [Fact]
    public async Task Analyst_CanReadTheRiskScoreReport_ViewerCannot()
    {
        // Cached rights live in SQL Server and outlast a test run, so they can predate script 10.
        using (var scope = _factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BlueTrack.Api.Auth.UserRightsCache>().InvalidateAllAsync();
        }

        Assert.Equal(HttpStatusCode.OK, (await GetAs("TestUser.Analyst")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await GetAs("TestUser.Viewer")).StatusCode);
    }
}
