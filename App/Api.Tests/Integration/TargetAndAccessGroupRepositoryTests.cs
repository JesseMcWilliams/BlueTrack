using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using Dapper;
using Microsoft.Data.SqlClient;
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

    /// <summary>D-121: TargetType filter narrows GetAllAsync's results; GetTotalCountAsync ignores it (a plain unfiltered COUNT(*)).</summary>
    [Fact]
    public async Task Target_GetAllAsync_FilterByType_NarrowsResults_ButTotalCountIgnoresFilter()
    {
        var repository = CreateTargetRepository();
        var serverKey = await repository.CreateAsync(new SaveTargetRequest
        {
            TargetType = "Server",
            TargetName = $"IntegrationTest_{Guid.NewGuid():N}",
            RiskScore = 100,
            Identifiers = []
        }, modifiedByUserKey: null);
        var appKey = await repository.CreateAsync(new SaveTargetRequest
        {
            TargetType = "Application",
            TargetName = $"IntegrationTest_{Guid.NewGuid():N}",
            RiskScore = 100,
            Identifiers = []
        }, modifiedByUserKey: null);

        try
        {
            var unfilteredCount = await repository.GetTotalCountAsync();
            var serverOnly = await repository.GetAllAsync(targetType: "Server");
            var filteredCount = await repository.GetTotalCountAsync();

            Assert.Contains(serverOnly, t => t.TargetKey == serverKey);
            Assert.DoesNotContain(serverOnly, t => t.TargetKey == appKey);
            Assert.Equal(unfilteredCount, filteredCount); // GetTotalCountAsync takes no filter params at all
        }
        finally
        {
            await repository.DeleteAsync(serverKey);
            await repository.DeleteAsync(appKey);
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

    /// <summary>D-121: SorTypeKey/SorAddress round-trip additively alongside GroupScope/FoundOnTargetKey (D-119), which stay untouched.</summary>
    [Fact]
    public async Task AccessGroup_SorFields_RoundTrip()
    {
        var repository = CreateAccessGroupRepository();
        var identifier = $"IntegrationTest_{Guid.NewGuid():N}";
        var sorTypes = await repository.GetSorTypesAsync();
        var localType = Assert.Single(sorTypes, t => t.SorTypeName == "Local");

        var accessGroupKey = await repository.CreateAsync(new SaveAccessGroupRequest
        {
            GroupName = "Integration Test SOR Group",
            GroupIdentifier = identifier,
            GroupScope = "Domain",
            SorTypeKey = localType.SorTypeKey,
            SorAddress = "192.168.1.1",
            BaseRiskScore = 100
        }, modifiedByUserKey: null);

        try
        {
            var all = await repository.GetAllAsync();
            var created = Assert.Single(all, g => g.AccessGroupKey == accessGroupKey);
            Assert.Equal(localType.SorTypeKey, created.SorTypeKey);
            Assert.Equal("Local", created.SorTypeName);
            Assert.Equal("192.168.1.1", created.SorAddress);
        }
        finally
        {
            await repository.DeleteAsync(accessGroupKey);
        }
    }

    /// <summary>D-121: GroupScope filter narrows GetAllAsync's results; GetTotalCountAsync ignores it (a plain unfiltered COUNT(*)).</summary>
    [Fact]
    public async Task AccessGroup_GetAllAsync_FilterByScope_NarrowsResults_ButTotalCountIgnoresFilter()
    {
        var repository = CreateAccessGroupRepository();
        var domainKey = await repository.CreateAsync(new SaveAccessGroupRequest
        {
            GroupName = "Integration Test Domain Filter Group",
            GroupIdentifier = $"IntegrationTest_{Guid.NewGuid():N}",
            GroupScope = "Domain",
            BaseRiskScore = 100
        }, modifiedByUserKey: null);
        var localKey = await repository.CreateAsync(new SaveAccessGroupRequest
        {
            GroupName = "Integration Test Local Filter Group",
            GroupIdentifier = $"IntegrationTest_{Guid.NewGuid():N}",
            GroupScope = "Local",
            BaseRiskScore = 100
        }, modifiedByUserKey: null);

        try
        {
            var unfilteredCount = await repository.GetTotalCountAsync();
            var localOnly = await repository.GetAllAsync(groupScope: "Local");
            var filteredCount = await repository.GetTotalCountAsync();

            Assert.Contains(localOnly, g => g.AccessGroupKey == localKey);
            Assert.DoesNotContain(localOnly, g => g.AccessGroupKey == domainKey);
            Assert.Equal(unfilteredCount, filteredCount);
        }
        finally
        {
            await repository.DeleteAsync(domainKey);
            await repository.DeleteAsync(localKey);
        }
    }

    [Fact]
    public async Task Target_UpdateAsync_RiskScoreChange_MarksReachingAccountsStale_ButLeavesUnrelatedAccountsAlone()
    {
        var targetRepository = CreateTargetRepository();
        var targetKey = await targetRepository.CreateAsync(new SaveTargetRequest
        {
            TargetType = "Server",
            TargetName = $"IntegrationTest_{Guid.NewGuid():N}",
            RiskScore = 300,
            Identifiers = []
        }, modifiedByUserKey: null);

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        var reachingAccountKey = await connection.QuerySingleAsync<long>(
            "INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted) OUTPUT inserted.AccountKey VALUES (1, @SourceAccountId, 'IntegrationTest Reaching Account', 0)",
            new { SourceAccountId = $"IntegrationTest_{Guid.NewGuid():N}" });
        var unrelatedAccountKey = await connection.QuerySingleAsync<long>(
            "INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted) OUTPUT inserted.AccountKey VALUES (1, @SourceAccountId, 'IntegrationTest Unrelated Account', 0)",
            new { SourceAccountId = $"IntegrationTest_{Guid.NewGuid():N}" });
        await connection.ExecuteAsync(
            "INSERT INTO web.account_target_map (AccountKey, TargetKey, SourceMethod) VALUES (@AccountKey, @TargetKey, 'ManualImport')",
            new { AccountKey = reachingAccountKey, TargetKey = targetKey });
        // Both accounts start with a non-stale row -- only the one actually reaching the edited Target should flip.
        await connection.ExecuteAsync(
            "INSERT INTO web.account_risk_score (AccountKey, ComputedRiskScore, IsRiskScoreStale) VALUES (@A1, 0, 0), (@A2, 0, 0)",
            new { A1 = reachingAccountKey, A2 = unrelatedAccountKey });

        try
        {
            await targetRepository.UpdateAsync(targetKey, new SaveTargetRequest
            {
                TargetType = "Server",
                TargetName = $"IntegrationTest_{Guid.NewGuid():N}",
                RiskScore = 850, // changed -- must cascade
                Identifiers = []
            }, modifiedByUserKey: null);

            var reachingIsStale = await connection.QuerySingleAsync<bool>(
                "SELECT IsRiskScoreStale FROM web.account_risk_score WHERE AccountKey = @AccountKey", new { AccountKey = reachingAccountKey });
            var unrelatedIsStale = await connection.QuerySingleAsync<bool>(
                "SELECT IsRiskScoreStale FROM web.account_risk_score WHERE AccountKey = @AccountKey", new { AccountKey = unrelatedAccountKey });

            Assert.True(reachingIsStale);
            Assert.False(unrelatedIsStale);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.account_target_map WHERE AccountKey = @AccountKey", new { AccountKey = reachingAccountKey });
            await connection.ExecuteAsync("DELETE FROM web.account_risk_score WHERE AccountKey IN (@A1, @A2)", new { A1 = reachingAccountKey, A2 = unrelatedAccountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE AccountKey IN (@A1, @A2)", new { A1 = reachingAccountKey, A2 = unrelatedAccountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey IN (@A1, @A2)", new { A1 = reachingAccountKey, A2 = unrelatedAccountKey });
            await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey IN (@A1, @A2)", new { A1 = reachingAccountKey, A2 = unrelatedAccountKey });
            await targetRepository.DeleteAsync(targetKey);
        }
    }

    [Fact]
    public async Task AccessGroup_UpdateAsync_BaseRiskScoreChange_MarksMemberAccountsStale()
    {
        var accessGroupRepository = CreateAccessGroupRepository();
        var identifier = $"IntegrationTest_{Guid.NewGuid():N}";
        var accessGroupKey = await accessGroupRepository.CreateAsync(new SaveAccessGroupRequest
        {
            GroupName = "Integration Test Stale Cascade Group",
            GroupIdentifier = identifier,
            GroupScope = "Domain",
            BaseRiskScore = 50
        }, modifiedByUserKey: null);

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        var memberAccountKey = await connection.QuerySingleAsync<long>(
            "INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted) OUTPUT inserted.AccountKey VALUES (1, @SourceAccountId, 'IntegrationTest Member Account', 0)",
            new { SourceAccountId = $"IntegrationTest_{Guid.NewGuid():N}" });
        await connection.ExecuteAsync(
            "INSERT INTO web.account_access_group_map (AccountKey, AccessGroupKey) VALUES (@AccountKey, @AccessGroupKey)",
            new { AccountKey = memberAccountKey, AccessGroupKey = accessGroupKey });
        await connection.ExecuteAsync(
            "INSERT INTO web.account_risk_score (AccountKey, ComputedRiskScore, IsRiskScoreStale) VALUES (@AccountKey, 0, 0)",
            new { AccountKey = memberAccountKey });

        try
        {
            await accessGroupRepository.UpdateAsync(accessGroupKey, new SaveAccessGroupRequest
            {
                GroupName = "Integration Test Stale Cascade Group",
                GroupIdentifier = identifier,
                GroupScope = "Domain",
                BaseRiskScore = 250 // changed -- must cascade to members
            }, modifiedByUserKey: null);

            var memberIsStale = await connection.QuerySingleAsync<bool>(
                "SELECT IsRiskScoreStale FROM web.account_risk_score WHERE AccountKey = @AccountKey", new { AccountKey = memberAccountKey });
            Assert.True(memberIsStale);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.account_access_group_map WHERE AccountKey = @AccountKey", new { AccountKey = memberAccountKey });
            await connection.ExecuteAsync("DELETE FROM web.account_risk_score WHERE AccountKey = @AccountKey", new { AccountKey = memberAccountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE AccountKey = @AccountKey", new { AccountKey = memberAccountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey = @AccountKey", new { AccountKey = memberAccountKey });
            await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey = @AccountKey", new { AccountKey = memberAccountKey });
            await accessGroupRepository.DeleteAsync(accessGroupKey);
        }
    }
}
