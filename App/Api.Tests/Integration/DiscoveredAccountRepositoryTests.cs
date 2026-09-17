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
    public async Task AcceptAsync_CreatesFactAccountAndProgressRow_ImmediatelyAtDiscoveredNotStarted()
    {
        var repository = CreateRepository();
        var reviewedByUserKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var objectSid = $"S-1-5-21-VERIFY-{Guid.NewGuid():N}";
        var discoveredAccountKey = await repository.UpsertCandidateAsync(new DiscoveredAccountCandidate
        {
            DomainName = "company.local",
            SamAccountName = $"accepttest_{Guid.NewGuid():N}",
            DisplayName = "Accept Test User",
            ObjectSid = objectSid,
            IsEnabled = true,
            MatchedAccessGroupKeys = []
        }, computedRiskScore: 0);

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        long accountKey = 0;

        try
        {
            accountKey = await repository.AcceptAsync(discoveredAccountKey, reviewedByUserKey);

            var account = await connection.QuerySingleAsync<(int SourceSystemKey, bool IsDeleted)>(
                "SELECT SourceSystemKey, IsDeleted FROM fact_account WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            var discoveryKey = await connection.QuerySingleAsync<int>("SELECT SourceSystemKey FROM dim_source_system WHERE SourceSystemCode = 'DISCOVERY'");
            Assert.Equal(discoveryKey, account.SourceSystemKey);
            Assert.False(account.IsDeleted);

            var progress = await connection.QuerySingleAsync<(int StageKey, int StatusKey)>(
                "SELECT CurrentStageKey, CurrentStatusKey FROM fact_account_progress WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            var discoveredStageKey = await connection.QuerySingleAsync<int>("SELECT StageKey FROM dim_blueprint_stage WHERE StageName = 'Discovered'");
            var notStartedStatusKey = await connection.QuerySingleAsync<int>("SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'Not Started'");
            Assert.Equal(discoveredStageKey, progress.StageKey);
            Assert.Equal(notStartedStatusKey, progress.StatusKey);

            var updated = await connection.QuerySingleAsync<(string Status, long? ResolvedAccountKey, int? ReviewedBy)>(
                "SELECT Status, ResolvedAccountKey, ReviewedBy FROM web.discovered_account WHERE DiscoveredAccountKey = @Key", new { Key = discoveredAccountKey });
            Assert.Equal("Accepted", updated.Status);
            Assert.Equal(accountKey, updated.ResolvedAccountKey);
            Assert.Equal(reviewedByUserKey, updated.ReviewedBy);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.discovered_account WHERE DiscoveredAccountKey = @Key", new { Key = discoveredAccountKey });
            if (accountKey != 0)
            {
                await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
                await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
                await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey = @AccountKey", new { AccountKey = accountKey });
            }
        }
    }

    [Fact]
    public async Task AcceptAsync_IsIdempotent_ReAcceptingReturnsSameAccountKey_NoDuplicate()
    {
        var repository = CreateRepository();
        var reviewedByUserKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var discoveredAccountKey = await repository.UpsertCandidateAsync(new DiscoveredAccountCandidate
        {
            DomainName = "company.local",
            SamAccountName = $"idempotenttest_{Guid.NewGuid():N}",
            ObjectSid = $"S-1-5-21-VERIFY-{Guid.NewGuid():N}",
            IsEnabled = true,
            MatchedAccessGroupKeys = []
        }, computedRiskScore: 0);

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        long firstAccountKey = 0;

        try
        {
            firstAccountKey = await repository.AcceptAsync(discoveredAccountKey, reviewedByUserKey);
            var secondAccountKey = await repository.AcceptAsync(discoveredAccountKey, reviewedByUserKey);

            Assert.Equal(firstAccountKey, secondAccountKey);
            var matchingRows = await connection.QuerySingleAsync<int>(
                "SELECT COUNT(*) FROM fact_account WHERE AccountKey = @AccountKey", new { AccountKey = firstAccountKey });
            Assert.Equal(1, matchingRows);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.discovered_account WHERE DiscoveredAccountKey = @Key", new { Key = discoveredAccountKey });
            if (firstAccountKey != 0)
            {
                await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE AccountKey = @AccountKey", new { AccountKey = firstAccountKey });
                await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey = @AccountKey", new { AccountKey = firstAccountKey });
                await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey = @AccountKey", new { AccountKey = firstAccountKey });
            }
        }
    }

    /// <summary>
    /// The specific regression Database/36_BlueTrack_FixAutoAdvanceForDiscoveredAccounts.sql
    /// exists to prevent: without that fix, a just-accepted DISCOVERY-sourced
    /// account (no Safe at all, since it isn't actually vaulted) would get
    /// wrongly auto-promoted straight to "Onboarded to Vault" on the very
    /// next Load run. A second, real PRIVCLOUD account with no Safe is also
    /// asserted to still advance -- proving the fix is scoped correctly, not
    /// just disabling the rule entirely.
    /// </summary>
    [Fact]
    public async Task AcceptAsync_ThenAutoAdvance_LeavesTheAcceptedAccountAtDiscovered_ButStillAdvancesARealAccountWithNoSafe()
    {
        var repository = CreateRepository();
        var reviewedByUserKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var discoveredAccountKey = await repository.UpsertCandidateAsync(new DiscoveredAccountCandidate
        {
            DomainName = "company.local",
            SamAccountName = $"autoadvancetest_{Guid.NewGuid():N}",
            ObjectSid = $"S-1-5-21-VERIFY-{Guid.NewGuid():N}",
            IsEnabled = true,
            MatchedAccessGroupKeys = []
        }, computedRiskScore: 0);

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();
        long discoveredSourcedAccountKey = 0;
        long privCloudAccountKey = 0;

        try
        {
            discoveredSourcedAccountKey = await repository.AcceptAsync(discoveredAccountKey, reviewedByUserKey);

            // Control: a real PRIVCLOUD account with no Safe, at the same
            // default Discovered/Not-Started state -- must still advance,
            // proving the fix didn't overcorrect.
            var discoveredStageKey = await connection.QuerySingleAsync<int>("SELECT StageKey FROM dim_blueprint_stage WHERE StageName = 'Discovered'");
            var notStartedStatusKey = await connection.QuerySingleAsync<int>("SELECT StatusKey FROM dim_progress_status WHERE StatusName = 'Not Started'");
            privCloudAccountKey = await connection.QuerySingleAsync<long>(
                "INSERT INTO fact_account (SourceSystemKey, SourceAccountId, AccountName, IsDeleted) OUTPUT inserted.AccountKey VALUES (1, @SourceAccountId, 'IntegrationTest AutoAdvance Control', 0)",
                new { SourceAccountId = $"IntegrationTest_{Guid.NewGuid():N}" });
            await connection.ExecuteAsync(
                "INSERT INTO fact_account_progress (AccountKey, CurrentStageKey, CurrentStatusKey) VALUES (@AccountKey, @StageKey, @StatusKey)",
                new { AccountKey = privCloudAccountKey, StageKey = discoveredStageKey, StatusKey = notStartedStatusKey });

            await connection.ExecuteAsync("EXEC usp_Load_AccountProgressAutoAdvance");

            var discoveredSourcedStageAfter = await connection.QuerySingleAsync<int>(
                "SELECT CurrentStageKey FROM fact_account_progress WHERE AccountKey = @AccountKey", new { AccountKey = discoveredSourcedAccountKey });
            var privCloudStageAfter = await connection.QuerySingleAsync<int>(
                "SELECT CurrentStageKey FROM fact_account_progress WHERE AccountKey = @AccountKey", new { AccountKey = privCloudAccountKey });
            var onboardedStageKey = await connection.QuerySingleAsync<int>("SELECT StageKey FROM dim_blueprint_stage WHERE StageName = 'Onboarded to Vault'");

            Assert.Equal(discoveredStageKey, discoveredSourcedStageAfter); // still Discovered -- the fix
            Assert.Equal(onboardedStageKey, privCloudStageAfter); // still advances -- proves no overcorrection
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.discovered_account WHERE DiscoveredAccountKey = @Key", new { Key = discoveredAccountKey });
            foreach (var key in new[] { discoveredSourcedAccountKey, privCloudAccountKey })
            {
                if (key == 0) continue;
                await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress_history WHERE AccountKey = @AccountKey", new { AccountKey = key });
                await connection.ExecuteAsync("DELETE FROM dbo.fact_account_progress WHERE AccountKey = @AccountKey", new { AccountKey = key });
                await connection.ExecuteAsync("DELETE FROM fact_account WHERE AccountKey = @AccountKey", new { AccountKey = key });
            }
        }
    }

    [Fact]
    public async Task DismissAsync_SetsStatusAndReviewer_WithNoFactAccountWrite()
    {
        var repository = CreateRepository();
        var reviewedByUserKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");
        var discoveredAccountKey = await repository.UpsertCandidateAsync(new DiscoveredAccountCandidate
        {
            DomainName = "company.local",
            SamAccountName = $"dismisstest_{Guid.NewGuid():N}",
            ObjectSid = $"S-1-5-21-VERIFY-{Guid.NewGuid():N}",
            IsEnabled = true,
            MatchedAccessGroupKeys = []
        }, computedRiskScore: 0);

        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.OpenAsync();

        try
        {
            await repository.DismissAsync(discoveredAccountKey, reviewedByUserKey);

            var row = await connection.QuerySingleAsync<(string Status, long? ResolvedAccountKey, int? ReviewedBy)>(
                "SELECT Status, ResolvedAccountKey, ReviewedBy FROM web.discovered_account WHERE DiscoveredAccountKey = @Key", new { Key = discoveredAccountKey });
            Assert.Equal("Dismissed", row.Status);
            Assert.Null(row.ResolvedAccountKey);
            Assert.Equal(reviewedByUserKey, row.ReviewedBy);
        }
        finally
        {
            await connection.ExecuteAsync("DELETE FROM web.discovered_account WHERE DiscoveredAccountKey = @Key", new { Key = discoveredAccountKey });
        }
    }

    [Fact]
    public async Task GetListAsync_DefaultsToStatusNew_ExcludingAcceptedAndDismissed()
    {
        var repository = CreateRepository();
        var reviewedByUserKey = await TestUsers.GetUserKeyAsync("IntegrationTestUser1");

        var newKey = await repository.UpsertCandidateAsync(new DiscoveredAccountCandidate
        {
            DomainName = "company.local", SamAccountName = $"newtest_{Guid.NewGuid():N}", IsEnabled = true, MatchedAccessGroupKeys = []
        }, computedRiskScore: 0);
        var dismissedKey = await repository.UpsertCandidateAsync(new DiscoveredAccountCandidate
        {
            DomainName = "company.local", SamAccountName = $"dismissedtest_{Guid.NewGuid():N}", IsEnabled = true, MatchedAccessGroupKeys = []
        }, computedRiskScore: 0);
        await repository.DismissAsync(dismissedKey, reviewedByUserKey);

        try
        {
            var defaultList = await repository.GetListAsync();
            Assert.Contains(defaultList, r => r.DiscoveredAccountKey == newKey);
            Assert.DoesNotContain(defaultList, r => r.DiscoveredAccountKey == dismissedKey);

            var everything = await repository.GetListAsync(status: null);
            Assert.Contains(everything, r => r.DiscoveredAccountKey == newKey);
            Assert.Contains(everything, r => r.DiscoveredAccountKey == dismissedKey);
        }
        finally
        {
            await using var connection = new SqlConnection(TestDatabase.ConnectionString);
            await connection.ExecuteAsync("DELETE FROM web.discovered_account WHERE DiscoveredAccountKey IN (@New, @Dismissed)", new { New = newKey, Dismissed = dismissedKey });
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
