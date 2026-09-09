using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.RiskScoring;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>web.dim_risk_score_band (D-120): named bands over the computed EffectiveRiskScore.</summary>
public class RiskScoreBandRepositoryTests
{
    private static RiskScoreBandRepository CreateRepository() => new(new TestDbConnectionFactory());

    [Fact]
    public async Task CreateAsync_ThenUpdateAsync_RoundTrips()
    {
        var repository = CreateRepository();
        var name = $"IntegrationTestBand_{Guid.NewGuid():N}"[..30];

        // A range far outside the seeded 0-1000 default bands (Low/Medium/High/Critical
        // cover the whole space) -- picking a range beyond 1000 keeps this test
        // independent of whatever the seeded/admin-edited bands currently look like.
        var bandKey = await repository.CreateAsync(new SaveRiskScoreBandRequest
        {
            BandName = name,
            MinScore = 2000,
            MaxScore = 2100,
            RiskOrder = 999
        }, modifiedByUserKey: null);

        try
        {
            var all = await repository.GetAllAsync();
            var created = Assert.Single(all, b => b.RiskScoreBandKey == bandKey);
            Assert.Equal(name, created.BandName);
            Assert.Equal(2000, created.MinScore);
            Assert.Equal(2100, created.MaxScore);
            Assert.Equal(999, created.RiskOrder);

            await repository.UpdateAsync(bandKey, new SaveRiskScoreBandRequest
            {
                BandName = name,
                MinScore = 2000,
                MaxScore = 2200, // widened -- still non-overlapping with anything else
                RiskOrder = 998
            }, modifiedByUserKey: null);

            var afterUpdate = await repository.GetAllAsync();
            var updated = Assert.Single(afterUpdate, b => b.RiskScoreBandKey == bandKey);
            Assert.Equal(2200, updated.MaxScore);
            Assert.Equal(998, updated.RiskOrder);
        }
        finally
        {
            await repository.DeleteAsync(bandKey);
        }
    }

    [Fact]
    public async Task CreateAsync_OverlappingRange_ThrowsRiskScoreBandOverlapException()
    {
        var repository = CreateRepository();
        var name = $"IntegrationTestBand_{Guid.NewGuid():N}"[..30];
        var bandKey = await repository.CreateAsync(new SaveRiskScoreBandRequest
        {
            BandName = name,
            MinScore = 3000,
            MaxScore = 3100,
            RiskOrder = 997
        }, modifiedByUserKey: null);

        try
        {
            await Assert.ThrowsAsync<RiskScoreBandOverlapException>(() => repository.CreateAsync(new SaveRiskScoreBandRequest
            {
                BandName = $"IntegrationTestBand_{Guid.NewGuid():N}"[..30],
                MinScore = 3050, // overlaps the existing 3000-3100 band
                MaxScore = 3150,
                RiskOrder = 996
            }, modifiedByUserKey: null));
        }
        finally
        {
            await repository.DeleteAsync(bandKey);
        }
    }

    [Fact]
    public async Task UpdateAsync_OverlappingAnotherBand_ThrowsRiskScoreBandOverlapException_ButNotAgainstItself()
    {
        var repository = CreateRepository();
        var firstKey = await repository.CreateAsync(new SaveRiskScoreBandRequest
        {
            BandName = $"IntegrationTestBand_{Guid.NewGuid():N}"[..30],
            MinScore = 4000,
            MaxScore = 4100,
            RiskOrder = 995
        }, modifiedByUserKey: null);
        var secondKey = await repository.CreateAsync(new SaveRiskScoreBandRequest
        {
            BandName = $"IntegrationTestBand_{Guid.NewGuid():N}"[..30],
            MinScore = 4200,
            MaxScore = 4300,
            RiskOrder = 994
        }, modifiedByUserKey: null);

        try
        {
            // Re-saving the second band with its own unchanged range must NOT throw
            // (excludingBandKey must exclude itself, not just any other row).
            await repository.UpdateAsync(secondKey, new SaveRiskScoreBandRequest
            {
                BandName = "Renamed but same range",
                MinScore = 4200,
                MaxScore = 4300,
                RiskOrder = 994
            }, modifiedByUserKey: null);

            // But expanding it to overlap the first band must throw.
            await Assert.ThrowsAsync<RiskScoreBandOverlapException>(() => repository.UpdateAsync(secondKey, new SaveRiskScoreBandRequest
            {
                BandName = "Renamed but same range",
                MinScore = 4050, // now overlaps the first band's 4000-4100
                MaxScore = 4300,
                RiskOrder = 994
            }, modifiedByUserKey: null));
        }
        finally
        {
            await repository.DeleteAsync(firstKey);
            await repository.DeleteAsync(secondKey);
        }
    }

    [Fact]
    public async Task GetAllAsync_OrdersByRiskOrder()
    {
        var repository = CreateRepository();
        var all = await repository.GetAllAsync();

        var orders = all.Select(b => b.RiskOrder).ToList();
        var sorted = orders.OrderBy(o => o).ToList();
        Assert.Equal(sorted, orders);
    }
}
