using System.Net;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>Layer 3: the three Reports sub-pages (D-56) and their permission gates.</summary>
public class ReportsControllerTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public ReportsControllerTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient CreateClientAs(string testUsername)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, testUsername);
        return client;
    }

    [Fact]
    public async Task GetOverdueAtRisk_AnyAuthenticatedUser_Succeeds()
    {
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.GetAsync("/api/reports/overdue-at-risk");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetStageStatusSummary_AnyAuthenticatedUser_Succeeds()
    {
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.GetAsync("/api/reports/stage-status-summary");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetReconciliationReviewQueue_WithoutConfirmReconciliation_IsForbidden()
    {
        // D-56: found ungated during frontend work, then fixed -- this is
        // the regression guard for that fix.
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.GetAsync("/api/reports/reconciliation-review-queue");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetReconciliationReviewQueue_AsAdmin_Succeeds()
    {
        var client = CreateClientAs("TestUser.Admin");

        var response = await client.GetAsync("/api/reports/reconciliation-review-queue");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AllReportEndpoints_Anonymous_AreUnauthorized()
    {
        var client = _factory.CreateClient();

        foreach (var path in new[] { "/api/reports/overdue-at-risk", "/api/reports/stage-status-summary", "/api/reports/reconciliation-review-queue" })
        {
            var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }
}
