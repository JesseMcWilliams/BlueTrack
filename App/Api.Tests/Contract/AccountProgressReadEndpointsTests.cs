using System.Net;
using System.Net.Http.Json;
using BlueTrack.Api.Tests.Integration;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// Layer 3: the read-only Account Progress endpoints that AccountProgressEditingTests
/// (locking/validation/risk-exception wiring) doesn't cover -- the list/grid
/// endpoint and the two form-support lookups any authenticated user can
/// read, plus the D-81 application-scoped exceptions endpoint.
/// </summary>
public class AccountProgressReadEndpointsTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public AccountProgressReadEndpointsTests(BlueTrackWebApplicationFactory factory)
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
    public async Task GetList_ReturnsTheSyntheticTestAccounts()
    {
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.GetAsync("/api/account-progress");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accounts = await response.Content.ReadFromJsonAsync<List<AccountSummaryResponse>>();
        Assert.NotNull(accounts);
        Assert.Contains(accounts!, a => a.AccountName == "TestAccount03");
    }

    /// <summary>
    /// D-121: X-Total-Count carries the unfiltered grand total (matches the
    /// bare-array body's own count when no filter is applied); the JSON
    /// body shape is unchanged. D-124 Phase 3: pageSize=500 is passed so
    /// the body still reflects the full set (now capped per page by
    /// default), and X-Filtered-Count is asserted equal to X-Total-Count
    /// too, since no filter is applied here.
    /// </summary>
    [Fact]
    public async Task GetList_SetsTotalCountHeader_MatchingBodyCountWhenUnfiltered()
    {
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.GetAsync("/api/account-progress?pageSize=500");

        Assert.True(response.Headers.TryGetValues("X-Total-Count", out var values));
        var accounts = await response.Content.ReadFromJsonAsync<List<AccountSummaryResponse>>();
        var total = int.Parse(values!.Single());
        Assert.Equal(accounts!.Count, total);

        Assert.True(response.Headers.TryGetValues("X-Filtered-Count", out var filteredValues));
        Assert.Equal(total, int.Parse(filteredValues!.Single()));
    }

    /// <summary>D-124 Phase 3: an owner filter that matches nothing narrows X-Filtered-Count to 0 while X-Total-Count stays the unfiltered grand total -- mirroring the equivalent "matches nothing" pattern already established for the Audit Log endpoint (AdminControllersFunctionalTests.AuditLog_GetEvents_SetsTotalCountHeader_IgnoringTheRequestsOwnFilter).</summary>
    [Fact]
    public async Task GetList_OwnerFilterMatchingNothing_NarrowsFilteredCount_ButTotalCountStaysUnfiltered()
    {
        var client = CreateClientAs("TestUser.Viewer");

        var unfilteredResponse = await client.GetAsync("/api/account-progress");
        var unfilteredTotal = int.Parse(unfilteredResponse.Headers.GetValues("X-Total-Count").Single());

        var filteredResponse = await client.GetAsync($"/api/account-progress?owner=this_owner_name_matches_nothing_{Guid.NewGuid():N}");
        var filteredBody = await filteredResponse.Content.ReadFromJsonAsync<List<AccountSummaryResponse>>();
        var filteredTotal = int.Parse(filteredResponse.Headers.GetValues("X-Total-Count").Single());
        var filteredFilteredCount = int.Parse(filteredResponse.Headers.GetValues("X-Filtered-Count").Single());

        Assert.Empty(filteredBody!);
        Assert.Equal(0, filteredFilteredCount);
        Assert.Equal(unfilteredTotal, filteredTotal);
    }

    [Fact]
    public async Task GetList_StageFilter_OnlyReturnsMatchingStage()
    {
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.GetAsync("/api/account-progress?stage=Discovered");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var accounts = await response.Content.ReadFromJsonAsync<List<AccountSummaryResponse>>();
        Assert.NotNull(accounts);
        Assert.All(accounts!, a => Assert.Equal("Discovered", a.StageName));
    }

    [Fact]
    public async Task GetFieldMetadata_AnyAuthenticatedUser_Succeeds()
    {
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.GetAsync("/api/account-progress/field-metadata");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetReferenceData_AnyAuthenticatedUser_ReturnsAllFiveTables()
    {
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.GetAsync("/api/account-progress/reference-data");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var referenceData = await response.Content.ReadFromJsonAsync<Dictionary<string, object>>();
        Assert.NotNull(referenceData);
        Assert.Equal(5, referenceData!.Count);
    }

    [Fact]
    public async Task GetApplicationScopedExceptions_NoneExist_ReturnsEmptyList()
    {
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount01");
        var client = CreateClientAs("TestUser.Viewer");

        var response = await client.GetAsync($"/api/account-progress/{accountKey}/application-exceptions");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var exceptions = await response.Content.ReadFromJsonAsync<List<ApplicationScopedExceptionResponse>>();
        Assert.Empty(exceptions!);
    }

    /// <summary>
    /// D-81: an Active exception scoped to an Application covers every
    /// account whose Safe is assigned to that Application (web.vw_account_application_exception),
    /// computed live rather than stored per-account.
    /// </summary>
    [Fact]
    public async Task GetApplicationScopedExceptions_AccountsSafeIsUnderAnExceptedApplication_IncludesIt()
    {
        var adminClient = CreateClientAs("TestUser.Admin");
        var accountKey = await TestAccounts.GetAccountKeyAsync("TestAccount01");
        var safeKey = await LookupSafeKeyForAccountAsync(accountKey);
        var suffix = Guid.NewGuid().ToString("N")[..12];

        var appCreateResponse = await adminClient.PostAsJsonAsync("/api/applications", new
        {
            applicationCode = $"CTAPPSCOPE{suffix}",
            applicationName = $"Application-Scoped Exception Test {suffix}"
        });
        Assert.Equal(HttpStatusCode.Created, appCreateResponse.StatusCode);
        var application = await appCreateResponse.Content.ReadFromJsonAsync<ApplicationKeyResponse>();

        try
        {
            var assignResponse = await adminClient.PutAsJsonAsync($"/api/safes/{safeKey}/application", application!.ApplicationKey);
            Assert.Equal(HttpStatusCode.NoContent, assignResponse.StatusCode);

            var approverClient = CreateClientAs("TestUser.Approver");
            var exceptionResponse = await approverClient.PostAsJsonAsync("/api/risk-exceptions", new
            {
                applicationKey = application.ApplicationKey,
                justification = "Application-scoped exception contract test",
                reviewDate = DateTime.UtcNow.Date.AddDays(30)
            });
            Assert.Equal(HttpStatusCode.Created, exceptionResponse.StatusCode);

            var response = await approverClient.GetAsync($"/api/account-progress/{accountKey}/application-exceptions");

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var exceptions = await response.Content.ReadFromJsonAsync<List<ApplicationScopedExceptionResponse>>();
            Assert.Contains(exceptions!, e => e.ApplicationKey == application.ApplicationKey);
        }
        finally
        {
            // The application itself is deliberately NOT deleted here:
            // web.risk_exception.ApplicationKey has a real FK to
            // web.dim_application, and RiskExceptionsController has no
            // Delete endpoint (confirmed elsewhere in this project), so the
            // exception just created would block that delete anyway. Only
            // the safe assignment is cleared, matching this shared
            // synthetic fixture's expected resting state.
            await adminClient.PutAsJsonAsync($"/api/safes/{safeKey}/application", (int?)null);
        }
    }

    private static async Task<int> LookupSafeKeyForAccountAsync(long accountKey)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.ConnectionString);
        return await Dapper.SqlMapper.QuerySingleAsync<int>(connection,
            "SELECT SafeKey FROM dbo.fact_account WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
    }

    private sealed class AccountSummaryResponse
    {
        public long AccountKey { get; set; }
        public string AccountName { get; set; } = "";
        public string StageName { get; set; } = "";
    }

    private sealed class ApplicationScopedExceptionResponse
    {
        public string ExceptionID { get; set; } = "";
        public int ApplicationKey { get; set; }
    }

    private sealed class ApplicationKeyResponse
    {
        public int ApplicationKey { get; set; }
    }
}
