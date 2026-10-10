using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.RiskScoring;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-122 (04_BlueTrack_Baseline_WebSchema.sql): an access group is
/// unique per (GroupName, GroupIdentifier, FoundOnTargetKey), not per
/// GroupIdentifier alone, so a well-known Local group's SID can repeat once
/// per server. Rebuilt 2026-10-09 from the never-merged branch
/// fix/access-group-duplicate-sid.
/// </summary>
public class AccessGroupUniquenessTests
{
    private static TargetRepository CreateTargetRepository() => new(new TestDbConnectionFactory());
    private static AccessGroupRepository CreateAccessGroupRepository() => new(new TestDbConnectionFactory());

    private static async Task<int> CreateServerAsync(TargetRepository repository)
    {
        var serverTypeKey = (await repository.GetTargetTypesAsync()).Single(t => t.TypeCode == "Server").TargetTypeKey;
        return await repository.CreateAsync(new SaveTargetRequest
        {
            TargetTypeKey = serverTypeKey, TargetName = $"IntegrationTest_{Guid.NewGuid():N}", RiskScore = 500, Identifiers = []
        }, modifiedByUserKey: null);
    }

    /// <summary>The reported bug: BUILTIN\Administrators carries the same SID on every server.</summary>
    [Fact]
    public async Task SameNameAndIdentifier_OnDifferentTargets_BothSucceed()
    {
        var targets = CreateTargetRepository();
        var groups = CreateAccessGroupRepository();
        var sharedIdentifier = $"S-1-5-32-544-IntegrationTest_{Guid.NewGuid():N}";
        var targetOne = await CreateServerAsync(targets);
        var targetTwo = await CreateServerAsync(targets);
        var created = new List<int>();

        try
        {
            foreach (var target in new[] { targetOne, targetTwo })
            {
                created.Add(await groups.CreateAsync(new SaveAccessGroupRequest
                {
                    GroupName = "Administrators", GroupIdentifier = sharedIdentifier, GroupScope = "Local", FoundOnTargetKey = target, BaseRiskScore = 500
                }, modifiedByUserKey: null));
            }
            Assert.NotNull(await groups.GetByKeyAsync(created[0]));
            Assert.NotNull(await groups.GetByKeyAsync(created[1]));
        }
        finally
        {
            foreach (var key in created) await groups.DeleteAsync(key);
            await targets.DeleteAsync(targetOne);
            await targets.DeleteAsync(targetTwo);
        }
    }

    [Fact]
    public async Task ExactDuplicate_OnSameTarget_Throws_OnCreateAndUpdate()
    {
        var targets = CreateTargetRepository();
        var groups = CreateAccessGroupRepository();
        var identifier = $"IntegrationTest_{Guid.NewGuid():N}";
        var target = await CreateServerAsync(targets);
        var created = new List<int>();

        try
        {
            SaveAccessGroupRequest Request(string name) => new()
            {
                GroupName = name, GroupIdentifier = identifier, GroupScope = "Local", FoundOnTargetKey = target, BaseRiskScore = 500
            };
            created.Add(await groups.CreateAsync(Request("Duplicate Test Group"), modifiedByUserKey: null));
            await Assert.ThrowsAsync<DuplicateAccessGroupException>(() => groups.CreateAsync(Request("Duplicate Test Group"), modifiedByUserKey: null));

            // Renaming another row onto an existing one is caught too.
            created.Add(await groups.CreateAsync(Request("Other Name"), modifiedByUserKey: null));
            await Assert.ThrowsAsync<DuplicateAccessGroupException>(() => groups.UpdateAsync(created[1], Request("Duplicate Test Group"), modifiedByUserKey: null));
            // ...while saving a row unchanged isn't a duplicate of itself.
            await groups.UpdateAsync(created[0], Request("Duplicate Test Group"), modifiedByUserKey: null);
        }
        finally
        {
            foreach (var key in created) await groups.DeleteAsync(key);
            await targets.DeleteAsync(target);
        }
    }

    /// <summary>Domain-scope groups have no FoundOnTargetKey (NULL); two NULLs must still count as the same.</summary>
    [Fact]
    public async Task ExactDuplicate_BothDomainScope_Throws()
    {
        var groups = CreateAccessGroupRepository();
        var identifier = $"IntegrationTest_{Guid.NewGuid():N}";
        var first = await groups.CreateAsync(new SaveAccessGroupRequest
        {
            GroupName = "Duplicate Domain Group", GroupIdentifier = identifier, GroupScope = "Domain", BaseRiskScore = 500
        }, modifiedByUserKey: null);

        try
        {
            await Assert.ThrowsAsync<DuplicateAccessGroupException>(() => groups.CreateAsync(new SaveAccessGroupRequest
            {
                GroupName = "Duplicate Domain Group", GroupIdentifier = identifier, GroupScope = "Domain", BaseRiskScore = 999
            }, modifiedByUserKey: null));
        }
        finally
        {
            await groups.DeleteAsync(first);
        }
    }

    /// <summary>The database enforces the same rule (and gives each row its own InternalGuid), independent of the app's check.</summary>
    [Fact]
    public async Task Database_HasTheCompositeConstraint_AndDistinctInternalGuids()
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        var constraints = (await connection.QueryAsync<string>(
            "SELECT name FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID('web.dim_access_group') AND type = 'UQ'")).ToList();
        Assert.Contains("UQ_dim_access_group_NameIdentifierFoundOn", constraints);
        Assert.Contains("UQ_dim_access_group_InternalGuid", constraints);
        Assert.DoesNotContain("UQ_dim_access_group", constraints);

        var groups = CreateAccessGroupRepository();
        var keys = new List<int>();
        try
        {
            foreach (var n in new[] { 1, 2 })
            {
                keys.Add(await groups.CreateAsync(new SaveAccessGroupRequest
                {
                    GroupName = $"InternalGuid Test {n}", GroupIdentifier = $"IntegrationTest_{Guid.NewGuid():N}", GroupScope = "Domain", BaseRiskScore = 500
                }, modifiedByUserKey: null));
            }
            var guids = (await connection.QueryAsync<Guid>("SELECT InternalGuid FROM web.dim_access_group WHERE AccessGroupKey IN @keys", new { keys })).ToList();
            Assert.Equal(2, guids.Distinct().Count());
            Assert.DoesNotContain(Guid.Empty, guids);
        }
        finally
        {
            foreach (var key in keys) await groups.DeleteAsync(key);
        }
    }
}
