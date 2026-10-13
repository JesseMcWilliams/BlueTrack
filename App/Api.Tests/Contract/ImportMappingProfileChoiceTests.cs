using System.Net;
using System.Net.Http.Json;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.RiskScoring;
using BlueTrack.Api.Tests.Integration;
using Xunit;

namespace BlueTrack.Api.Tests.Contract;

/// <summary>
/// D-207: no mapping profile chosen means the CSV's own column names -- it used to
/// fall back to the oldest active profile for the feed type -- and the Bulk Actions
/// pages list the active profiles to choose from.
/// </summary>
public class ImportMappingProfileChoiceTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public ImportMappingProfileChoiceTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private HttpClient Client(string user)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TestUserHeaderName, user);
        return client;
    }

    private sealed record ProfileOption(int ImportMappingProfileKey, string ProfileName);

    [Fact]
    public async Task NoProfileChosen_UsesTheCsvColumns_EvenWithAnActiveProfile_AndTheListOffersIt()
    {
        var repository = new ImportMappingProfileRepository(new TestDbConnectionFactory());
        var name = $"ContractTest {Guid.NewGuid():N}"[..30];
        var key = await repository.CreateAsync(new SaveImportMappingProfileRequest
        {
            FeedType = "TargetInventory",
            ProfileName = name,
            IsActive = true,
            Fields = [new SaveImportMappingFieldRequest { SourceColumnName = "Host", TargetFieldName = "TargetName", IsRequired = true }]
        }, null);
        try
        {
            var service = new ImportMappingService(new TestDbConnectionFactory());
            Assert.Empty(await service.GetActiveMappingAsync("TargetInventory", null));
            Assert.Equal("Host", (await service.GetActiveMappingAsync("TargetInventory", key))["TargetName"].SourceColumnName);

            var options = await Client("TestUser.Admin").GetFromJsonAsync<List<ProfileOption>>(
                "/api/admin/risk-scoring/import/target-mapping-profiles?feedType=TargetInventory");
            Assert.Contains(options!, o => o.ImportMappingProfileKey == key && o.ProfileName == name);
        }
        finally
        {
            await repository.DeleteAsync(key);
        }
    }

    [Fact]
    public async Task TheProfileLists_CheckTheFeedType_AndTheImportsPermission()
    {
        var admin = Client("TestUser.Admin");
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.GetAsync("/api/admin/risk-scoring/import/target-mapping-profiles?feedType=AccessGroupInventory")).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await admin.GetAsync("/api/admin/risk-scoring/import/access-group-mapping-profiles?feedType=AccessGroupInventory")).StatusCode);

        var viewer = Client("TestUser.Viewer");
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.GetAsync("/api/admin/risk-scoring/import/target-mapping-profiles?feedType=TargetInventory")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await viewer.GetAsync("/api/admin/risk-scoring/import/access-group-mapping-profiles?feedType=AccessGroupInventory")).StatusCode);
    }
}
