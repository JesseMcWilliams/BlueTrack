using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// AD Account Discovery feature (2026-09-16), Database/33: web.discovered_account
/// and web.discovered_account_access_group_map. AdAccountDiscoveryService's
/// own AD-interaction logic isn't unit tested here -- this app has no
/// existing precedent for mocking System.DirectoryServices.AccountManagement
/// (LdapGroupMemberResolver itself has none either, only live verification),
/// so that stays a stated live-verification-only limitation. What's covered
/// here is everything genuinely testable without a real domain: the
/// best-effort onboarded-account matching, the upsert/replace-all-on-save
/// persistence, and the AccessGroupSet risk-scoring call through the
/// repository's own wrapper (not just the raw procedure, already covered by
/// RiskScoreCalculationForAccessGroupSetTests).
/// </summary>
public class DiscoveredAccountRepositoryTests
{
    private static DiscoveredAccountRepository CreateRepository() => new(new TestDbConnectionFactory());

    [Fact]
    public async Task FindPossibleExistingAccountKeyAsync_MatchesBareUsername()
    {
        var repository = CreateRepository();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        var samAccountName = $"jsmith_{Guid.NewGuid():N}";
        var accountKey = await connection.QuerySingleAsync<long>(
            "INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, UserName, IsDeleted) OUTPUT inserted.AccountKey VALUES (1, @SourceAccountId, 'IntegrationTest Bare', @UserName, 0)",
            new { SourceAccountId = $"IntegrationTest_{Guid.NewGuid():N}", UserName = samAccountName });

        try
        {
            Assert.Equal(accountKey, await repository.FindPossibleExistingAccountKeyAsync("company.local", samAccountName));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
        }
    }

    [Fact]
    public async Task FindPossibleExistingAccountKeyAsync_MatchesWhenUserNameIsStoredAsDomainSlashUsername()
    {
        var repository = CreateRepository();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        var samAccountName = $"jdoe_{Guid.NewGuid():N}";
        var domainName = "company.local";
        var accountKey = await connection.QuerySingleAsync<long>(
            "INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, UserName, IsDeleted) OUTPUT inserted.AccountKey VALUES (1, @SourceAccountId, 'IntegrationTest DomainSlash Only', @UserName, 0)",
            new { SourceAccountId = $"IntegrationTest_{Guid.NewGuid():N}", UserName = $"{domainName}\\{samAccountName}" });

        try
        {
            Assert.Equal(accountKey, await repository.FindPossibleExistingAccountKeyAsync(domainName, samAccountName));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
        }
    }

    [Fact]
    public async Task FindPossibleExistingAccountKeyAsync_MatchesWhenUserNameIsStoredAsUsernameAtDomain()
    {
        var repository = CreateRepository();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        var samAccountName = $"asmith_{Guid.NewGuid():N}";
        var domainName = "company.local";
        var accountKey = await connection.QuerySingleAsync<long>(
            "INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, UserName, IsDeleted) OUTPUT inserted.AccountKey VALUES (1, @SourceAccountId, 'IntegrationTest UserAtDomain Only', @UserName, 0)",
            new { SourceAccountId = $"IntegrationTest_{Guid.NewGuid():N}", UserName = $"{samAccountName}@{domainName}" });

        try
        {
            Assert.Equal(accountKey, await repository.FindPossibleExistingAccountKeyAsync(domainName, samAccountName));
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
        }
    }

    [Fact]
    public async Task FindPossibleExistingAccountKeyAsync_ReturnsNull_WhenNoMatch()
    {
        var repository = CreateRepository();
        var result = await repository.FindPossibleExistingAccountKeyAsync("company.local", $"NoSuchUser_{Guid.NewGuid():N}");
        Assert.Null(result);
    }

    [Fact]
    public async Task UpsertCandidateAsync_RerunUpdatesExistingRow_RatherThanDuplicating()
    {
        var repository = CreateRepository();
        var domainName = $"IntegrationTest_{Guid.NewGuid():N}";
        var samAccountName = "verifyuser";

        var firstKey = await repository.UpsertCandidateAsync(new DiscoveredAccountCandidate
        {
            DomainName = domainName,
            SamAccountName = samAccountName,
            DisplayName = "Verify User",
            IsEnabled = true,
            MatchedAccessGroupKeys = []
        }, computedRiskScore: 100);

        try
        {
            var secondKey = await repository.UpsertCandidateAsync(new DiscoveredAccountCandidate
            {
                DomainName = domainName,
                SamAccountName = samAccountName,
                DisplayName = "Verify User Renamed",
                IsEnabled = false,
                MatchedAccessGroupKeys = []
            }, computedRiskScore: 250);

            Assert.Equal(firstKey, secondKey);

            var list = await repository.GetListAsync();
            var row = Assert.Single(list, r => r.DiscoveredAccountKey == firstKey);
            Assert.Equal("Verify User Renamed", row.DisplayName);
            Assert.False(row.IsEnabled);
            Assert.Equal(250, row.ComputedRiskScore);
        }
        finally
        {
            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            await connection.ExecuteAsync("DELETE FROM web.discovered_account_access_group_map WHERE DiscoveredAccountKey = @Key", new { Key = firstKey });
            await connection.ExecuteAsync("DELETE FROM web.discovered_account WHERE DiscoveredAccountKey = @Key", new { Key = firstKey });
        }
    }

    [Fact]
    public async Task ReplaceAccessGroupMappingsAsync_ReplacesRatherThanAppends()
    {
        var repository = CreateRepository();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        var groupKey1 = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_access_group (GroupName, GroupIdentifier, GroupScope, BaseRiskScore) OUTPUT inserted.AccessGroupKey VALUES ('IntegrationTest DA Group1', @Identifier, 'Domain', 10)",
            new { Identifier = $"IntegrationTest_{Guid.NewGuid():N}" });
        var groupKey2 = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_access_group (GroupName, GroupIdentifier, GroupScope, BaseRiskScore) OUTPUT inserted.AccessGroupKey VALUES ('IntegrationTest DA Group2', @Identifier, 'Domain', 20)",
            new { Identifier = $"IntegrationTest_{Guid.NewGuid():N}" });

        var discoveredAccountKey = await repository.UpsertCandidateAsync(new DiscoveredAccountCandidate
        {
            DomainName = $"IntegrationTest_{Guid.NewGuid():N}",
            SamAccountName = "replacetest",
            IsEnabled = true,
            MatchedAccessGroupKeys = []
        }, computedRiskScore: 0);

        try
        {
            await repository.ReplaceAccessGroupMappingsAsync(discoveredAccountKey, [groupKey1, groupKey2]);
            var afterFirst = await repository.GetMatchedAccessGroupsAsync(discoveredAccountKey);
            Assert.Equal(2, afterFirst.Count);

            await repository.ReplaceAccessGroupMappingsAsync(discoveredAccountKey, [groupKey1]);
            var afterSecond = await repository.GetMatchedAccessGroupsAsync(discoveredAccountKey);
            Assert.Single(afterSecond);
            Assert.Equal(groupKey1, afterSecond[0].AccessGroupKey);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.discovered_account_access_group_map WHERE DiscoveredAccountKey = @Key", new { Key = discoveredAccountKey });
            await connection.ExecuteAsync("DELETE FROM web.discovered_account WHERE DiscoveredAccountKey = @Key", new { Key = discoveredAccountKey });
            await connection.ExecuteAsync("DELETE FROM web.dim_access_group WHERE AccessGroupKey IN (@G1, @G2)", new { G1 = groupKey1, G2 = groupKey2 });
        }
    }

    [Fact]
    public async Task CalculateRiskScoreAsync_MatchesHandCalculatedValue_ViaTheRepositoryWrapper()
    {
        var repository = CreateRepository();
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        // Same hand-built graph as RiskScoreCalculationForAccessGroupSetTests:
        // Group (Base=100) -> Targets (400, 200) => DominantPlusTail 496.
        var targetKey1 = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore) OUTPUT inserted.TargetKey VALUES ((SELECT TargetTypeKey FROM web.dim_target_type WHERE TypeCode = 'Server'), @Name, 400)",
            new { Name = $"IntegrationTest_{Guid.NewGuid():N}" });
        var targetKey2 = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_target (TargetTypeKey, TargetName, RiskScore) OUTPUT inserted.TargetKey VALUES ((SELECT TargetTypeKey FROM web.dim_target_type WHERE TypeCode = 'Server'), @Name, 200)",
            new { Name = $"IntegrationTest_{Guid.NewGuid():N}" });
        var groupKey = await connection.QuerySingleAsync<int>(
            "INSERT INTO web.dim_access_group (GroupName, GroupIdentifier, GroupScope, BaseRiskScore) OUTPUT inserted.AccessGroupKey VALUES ('IntegrationTest DA Score Group', @Identifier, 'Domain', 100)",
            new { Identifier = $"IntegrationTest_{Guid.NewGuid():N}" });
        await connection.ExecuteAsync(
            "INSERT INTO web.access_group_target_map (AccessGroupKey, TargetKey) VALUES (@GroupKey, @T1), (@GroupKey, @T2)",
            new { GroupKey = groupKey, T1 = targetKey1, T2 = targetKey2 });

        try
        {
            var score = await repository.CalculateRiskScoreAsync([groupKey]);
            Assert.True(score is 496 or 568, $"Expected whichever algorithm is active (496 DominantPlusTail or 568 CombinedExposure), got {score}.");
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.access_group_target_map WHERE AccessGroupKey = @GroupKey", new { GroupKey = groupKey });
            await connection.ExecuteAsync("DELETE FROM web.dim_access_group WHERE AccessGroupKey = @GroupKey", new { GroupKey = groupKey });
            await connection.ExecuteAsync("DELETE FROM web.dim_target WHERE TargetKey IN (@T1, @T2)", new { T1 = targetKey1, T2 = targetKey2 });
        }
    }
}
