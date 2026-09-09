using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>web.dim_target/web.target_identifier/web.dim_access_group (Design_Risk_Scoring.md, D-101-105, Phase A).</summary>
public class TargetAndAccessGroupRepositoryTests
{
    private static TargetRepository CreateTargetRepository() => new(new TestDbConnectionFactory());
    private static AccessGroupRepository CreateAccessGroupRepository() => new(new TestDbConnectionFactory());

    [Fact]
    public async Task Target_CreateAsync_WithMultipleIdentifiers_RoundTripsAll()
    {
        var repository = CreateTargetRepository();
        var name = $"IntegrationTest_{Guid.NewGuid():N}";

        var targetKey = await repository.CreateAsync(new SaveTargetRequest
        {
            TargetType = "Server",
            TargetName = name,
            RiskScore = 900,
            Identifiers =
            [
                new SaveTargetIdentifierRequest { IdentifierType = "Hostname", IdentifierValue = $"host-{Guid.NewGuid():N}" },
                new SaveTargetIdentifierRequest { IdentifierType = "FQDN", IdentifierValue = $"fqdn-{Guid.NewGuid():N}.example.com" }
            ]
        }, modifiedByUserKey: null);

        try
        {
            var all = await repository.GetAllAsync();
            var created = Assert.Single(all, t => t.TargetKey == targetKey);
            Assert.Equal(900, created.RiskScore);
            Assert.Equal(2, created.Identifiers.Count);
            Assert.Contains(created.Identifiers, i => i.IdentifierType == "Hostname");
            Assert.Contains(created.Identifiers, i => i.IdentifierType == "FQDN");
        }
        finally
        {
            await repository.DeleteAsync(targetKey);
        }
    }

    [Fact]
    public async Task Target_UpdateAsync_ReplacesIdentifiers_NotAppends()
    {
        var repository = CreateTargetRepository();
        var name = $"IntegrationTest_{Guid.NewGuid():N}";
        var targetKey = await repository.CreateAsync(new SaveTargetRequest
        {
            TargetType = "Server",
            TargetName = name,
            RiskScore = 100,
            Identifiers = [new SaveTargetIdentifierRequest { IdentifierType = "Hostname", IdentifierValue = $"original-{Guid.NewGuid():N}" }]
        }, modifiedByUserKey: null);

        try
        {
            var newIdentifierValue = $"replacement-{Guid.NewGuid():N}";
            await repository.UpdateAsync(targetKey, new SaveTargetRequest
            {
                TargetType = "Server",
                TargetName = name,
                RiskScore = 100,
                Identifiers = [new SaveTargetIdentifierRequest { IdentifierType = "FQDN", IdentifierValue = newIdentifierValue }]
            }, modifiedByUserKey: null);

            var all = await repository.GetAllAsync();
            var updated = Assert.Single(all, t => t.TargetKey == targetKey);
            var identifier = Assert.Single(updated.Identifiers);
            Assert.Equal("FQDN", identifier.IdentifierType);
            Assert.Equal(newIdentifierValue, identifier.IdentifierValue);
        }
        finally
        {
            await repository.DeleteAsync(targetKey);
        }
    }

    [Fact]
    public async Task Target_IdentifierValue_MustBeUniqueAcrossTargets()
    {
        var repository = CreateTargetRepository();
        var sharedValue = $"shared-{Guid.NewGuid():N}";
        var firstKey = await repository.CreateAsync(new SaveTargetRequest
        {
            TargetType = "Server",
            TargetName = $"IntegrationTest_{Guid.NewGuid():N}",
            RiskScore = 100,
            Identifiers = [new SaveTargetIdentifierRequest { IdentifierType = "ADGuid", IdentifierValue = sharedValue }]
        }, modifiedByUserKey: null);

        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => repository.CreateAsync(new SaveTargetRequest
            {
                TargetType = "Server",
                TargetName = $"IntegrationTest_{Guid.NewGuid():N}",
                RiskScore = 100,
                Identifiers = [new SaveTargetIdentifierRequest { IdentifierType = "ADGuid", IdentifierValue = sharedValue }]
            }, modifiedByUserKey: null));
        }
        finally
        {
            await repository.DeleteAsync(firstKey);
        }
    }

    [Fact]
    public async Task AccessGroup_CreateAsync_StartsRiskScoreStale_WithNullComputedScore()
    {
        var repository = CreateAccessGroupRepository();
        var identifier = $"IntegrationTest_{Guid.NewGuid():N}";

        var accessGroupKey = await repository.CreateAsync(new SaveAccessGroupRequest
        {
            GroupName = "Integration Test Group",
            GroupIdentifier = identifier,
            GroupScope = "Domain",
            BaseRiskScore = 400
        }, modifiedByUserKey: null);

        try
        {
            var all = await repository.GetAllAsync();
            var created = Assert.Single(all, g => g.AccessGroupKey == accessGroupKey);
            Assert.True(created.IsRiskScoreStale);
            Assert.Null(created.ComputedRiskScore);
        }
        finally
        {
            await repository.DeleteAsync(accessGroupKey);
        }
    }

    [Fact]
    public async Task AccessGroup_LocalScope_WithFoundOnTarget_RoundTripsTargetName()
    {
        var targetRepository = CreateTargetRepository();
        var accessGroupRepository = CreateAccessGroupRepository();
        var targetKey = await targetRepository.CreateAsync(new SaveTargetRequest
        {
            TargetType = "Server",
            TargetName = $"IntegrationTest_{Guid.NewGuid():N}",
            RiskScore = 700,
            Identifiers = []
        }, modifiedByUserKey: null);

        try
        {
            var accessGroupKey = await accessGroupRepository.CreateAsync(new SaveAccessGroupRequest
            {
                GroupName = "Integration Test Local Admins",
                GroupIdentifier = $"IntegrationTest_{Guid.NewGuid():N}",
                GroupScope = "Local",
                FoundOnTargetKey = targetKey,
                BaseRiskScore = 500
            }, modifiedByUserKey: null);

            try
            {
                var all = await accessGroupRepository.GetAllAsync();
                var created = Assert.Single(all, g => g.AccessGroupKey == accessGroupKey);
                Assert.Equal(targetKey, created.FoundOnTargetKey);
                Assert.NotNull(created.FoundOnTargetName);
            }
            finally
            {
                await accessGroupRepository.DeleteAsync(accessGroupKey);
            }
        }
        finally
        {
            await targetRepository.DeleteAsync(targetKey);
        }
    }
}
