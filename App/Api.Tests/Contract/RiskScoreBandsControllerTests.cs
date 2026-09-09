using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-120: Risk Score Bands admin CRUD, gated by ManageRiskScoreBands. All
/// state created here is cleaned up by the test that created it.
/// </summary>
public class RiskScoreBandsControllerTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public RiskScoreBandsControllerTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient AdminClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, "TestUser.Admin");
        return client;
    }

    [Fact]
    public async Task Band_CreateUpdateDelete_RoundTrips()
    {
        var client = AdminClient();
        var name = $"ContractTestBand_{Guid.NewGuid():N}"[..30];

        var createResponse = await client.PostAsJsonAsync("/api/admin/risk-score-bands", new
        {
            bandName = name,
            minScore = 5000,
            maxScore = 5100,
            riskOrder = 993
        });
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await createResponse.Content.ReadFromJsonAsync<BandKeyResponse>();

        try
        {
            var list = await client.GetFromJsonAsync<List<BandResponse>>("/api/admin/risk-score-bands");
            var row = Assert.Single(list!, b => b.RiskScoreBandKey == created!.RiskScoreBandKey);
            Assert.Equal(name, row.BandName);
            Assert.Equal(5000, row.MinScore);
            Assert.Equal(5100, row.MaxScore);

            var updateResponse = await client.PutAsJsonAsync($"/api/admin/risk-score-bands/{created!.RiskScoreBandKey}", new
            {
                bandName = name,
                minScore = 5000,
                maxScore = 5150,
                riskOrder = 992
            });
            Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);

            var afterUpdate = await client.GetFromJsonAsync<List<BandResponse>>("/api/admin/risk-score-bands");
            var updated = Assert.Single(afterUpdate!, b => b.RiskScoreBandKey == created.RiskScoreBandKey);
            Assert.Equal(5150, updated.MaxScore);
            Assert.Equal(992, updated.RiskOrder);
        }
        finally
        {
            var deleteResponse = await client.DeleteAsync($"/api/admin/risk-score-bands/{created!.RiskScoreBandKey}");
            Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        }
    }

    [Fact]
    public async Task Band_Create_OverlappingRange_Returns400()
    {
        var client = AdminClient();
        var firstCreate = await client.PostAsJsonAsync("/api/admin/risk-score-bands", new
        {
            bandName = $"ContractTestBand_{Guid.NewGuid():N}"[..30],
            minScore = 6000,
            maxScore = 6100,
            riskOrder = 991
        });
        Assert.Equal(HttpStatusCode.Created, firstCreate.StatusCode);
        var first = await firstCreate.Content.ReadFromJsonAsync<BandKeyResponse>();

        try
        {
            var overlappingCreate = await client.PostAsJsonAsync("/api/admin/risk-score-bands", new
            {
                bandName = $"ContractTestBand_{Guid.NewGuid():N}"[..30],
                minScore = 6050, // overlaps 6000-6100
                maxScore = 6150,
                riskOrder = 990
            });
            Assert.Equal(HttpStatusCode.BadRequest, overlappingCreate.StatusCode);
        }
        finally
        {
            await client.DeleteAsync($"/api/admin/risk-score-bands/{first!.RiskScoreBandKey}");
        }
    }

    // The ManageRiskScoreBands gate itself is covered by
    // AdminControllersPermissionTests' shared GatedGetEndpoints matrix
    // (Viewer-forbidden/Admin-succeeds/anonymous-unauthorized), same as
    // Targets/AccessGroups -- not duplicated here.

    private sealed class BandKeyResponse
    {
        public int RiskScoreBandKey { get; set; }
    }

    private sealed class BandResponse
    {
        public int RiskScoreBandKey { get; set; }
        public string BandName { get; set; } = "";
        public int MinScore { get; set; }
        public int MaxScore { get; set; }
        public int RiskOrder { get; set; }
    }
}
