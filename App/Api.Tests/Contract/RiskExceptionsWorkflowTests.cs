using System.Net;
using System.Net.Http.Json;
using BlueTrack.Api.Tests.Integration;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// Layer 3: the create/extend-review/revoke workflow
/// (Design_Risk_Exception_Tracking.md) over real HTTP, plus the
/// exactly-one-of-AccountKey/ApplicationKey validation rule and
/// permission enforcement per action.
/// </summary>
public class RiskExceptionsWorkflowTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public RiskExceptionsWorkflowTests(BlueTrackWebApplicationFactory factory)
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
    public async Task Create_NeitherAccountNorApplicationSet_ReturnsBadRequest()
    {
        var client = CreateClientAs("TestUser.Approver");

        var response = await client.PostAsJsonAsync("/api/risk-exceptions", new
        {
            justification = "Missing scope",
            reviewDate = DateTime.UtcNow.Date.AddDays(30)
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_BothAccountAndApplicationSet_ReturnsBadRequest()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var client = CreateClientAs("TestUser.Approver");

        var response = await client.PostAsJsonAsync("/api/risk-exceptions", new
        {
            accountKey,
            applicationKey = 1,
            justification = "Both scopes set",
            reviewDate = DateTime.UtcNow.Date.AddDays(30)
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Create_AsApprover_SucceedsAndIsRetrievable()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var client = CreateClientAs("TestUser.Approver");

        var createResponse = await client.PostAsJsonAsync("/api/risk-exceptions", new
        {
            accountKey,
            justification = "Contract test exception",
            reviewDate = DateTime.UtcNow.Date.AddDays(30)
        });

        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var body = await createResponse.Content.ReadFromJsonAsync<CreatedExceptionResponse>();
        Assert.NotNull(body);

        var getResponse = await client.GetAsync($"/api/risk-exceptions/{body!.ExceptionKey}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
    }

    [Fact]
    public async Task Create_AsAnalystWithoutApproveExceptions_IsForbidden()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var client = CreateClientAs("TestUser.Analyst");

        var response = await client.PostAsJsonAsync("/api/risk-exceptions", new
        {
            accountKey,
            justification = "Should be rejected",
            reviewDate = DateTime.UtcNow.Date.AddDays(30)
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExtendReview_AsApprover_UpdatesReviewDate()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var client = CreateClientAs("TestUser.Approver");
        var created = await CreateExceptionAsync(client, accountKey);
        var newReviewDate = DateTime.UtcNow.Date.AddDays(120);

        var response = await client.PutAsJsonAsync($"/api/risk-exceptions/{created}/extend-review", new
        {
            newReviewDate
        });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ExtendReview_AsViewer_IsForbidden()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var approverClient = CreateClientAs("TestUser.Approver");
        var created = await CreateExceptionAsync(approverClient, accountKey);
        var viewerClient = CreateClientAs("TestUser.Viewer");

        var response = await viewerClient.PutAsJsonAsync($"/api/risk-exceptions/{created}/extend-review", new
        {
            newReviewDate = DateTime.UtcNow.Date.AddDays(60)
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task ExtendReview_UnknownExceptionKey_ReturnsNotFound()
    {
        var client = CreateClientAs("TestUser.Approver");

        var response = await client.PutAsJsonAsync("/api/risk-exceptions/999999/extend-review", new
        {
            newReviewDate = DateTime.UtcNow.Date.AddDays(60)
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Revoke_AsApprover_SetsStatusToRevoked()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var client = CreateClientAs("TestUser.Approver");
        var created = await CreateExceptionAsync(client, accountKey);

        var revokeResponse = await client.PutAsync($"/api/risk-exceptions/{created}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revokeResponse.StatusCode);

        var detail = await client.GetFromJsonAsync<RiskExceptionDetailResponse>($"/api/risk-exceptions/{created}");
        Assert.Equal("Revoked", detail!.StatusName);
    }

    [Fact]
    public async Task Revoke_AsAnalyst_IsForbidden()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount03");
        var approverClient = CreateClientAs("TestUser.Approver");
        var created = await CreateExceptionAsync(approverClient, accountKey);
        var analystClient = CreateClientAs("TestUser.Analyst");

        var response = await analystClient.PutAsync($"/api/risk-exceptions/{created}/revoke", null);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetList_AsViewer_Succeeds_NoApproveExceptionsNeeded()
    {
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.GetAsync("/api/risk-exceptions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetOverdueReview_AsViewer_Succeeds_NoApproveExceptionsNeeded()
    {
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.GetAsync("/api/risk-exceptions/overdue-review");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetByKey_UnknownKey_ReturnsNotFound()
    {
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.GetAsync("/api/risk-exceptions/999999");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<int> CreateExceptionAsync(HttpClient client, long accountKey)
    {
        var response = await client.PostAsJsonAsync("/api/risk-exceptions", new
        {
            accountKey,
            justification = "Fixture exception for a dependent test",
            reviewDate = DateTime.UtcNow.Date.AddDays(30)
        });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<CreatedExceptionResponse>();
        return body!.ExceptionKey;
    }

    private sealed class CreatedExceptionResponse
    {
        public int ExceptionKey { get; set; }
    }

    private sealed class RiskExceptionDetailResponse
    {
        public string StatusName { get; set; } = "";
    }
}
