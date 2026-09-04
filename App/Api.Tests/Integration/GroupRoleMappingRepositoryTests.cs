using BlueTrack.Api.Data;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// web.identity_group_role_map backs the Group → Role Mapping admin page.
/// Only WindowsIntegrated is a functional provider (see the repository's
/// own doc comment), so tests use a real WindowsIntegrated ProviderKey --
/// the repository itself only exposes GetRoleKeyByNameAsync as a lookup,
/// so the provider key comes from a direct SQL query instead.
/// </summary>
public class GroupRoleMappingRepositoryTests
{
    private static GroupRoleMappingRepository CreateRepository() => new(new TestDbConnectionFactory());

    [Fact]
    public async Task GetRoleKeyByNameAsync_KnownRole_ReturnsItsKey()
    {
        var repository = CreateRepository();

        var roleKey = await repository.GetRoleKeyByNameAsync("Viewer");

        Assert.NotNull(roleKey);
    }

    [Fact]
    public async Task GetRoleKeyByNameAsync_UnknownRole_ReturnsNull()
    {
        var repository = CreateRepository();

        var roleKey = await repository.GetRoleKeyByNameAsync("NoSuchRole_IntegrationTest_9f8e7d");

        Assert.Null(roleKey);
    }

    [Fact]
    public async Task CreateAsync_IsReadableByGetAllAsync_ThenDeleteAsync_RemovesIt()
    {
        var repository = CreateRepository();
        var providerKey = await GetWindowsIntegratedProviderKeyAsync();
        var roleKey = await repository.GetRoleKeyByNameAsync("Viewer");
        Assert.NotNull(roleKey);
        var sid = $"S-1-5-21-1111111111-2222222222-3333333333-{Random.Shared.Next(1000, 9999)}";

        var mappingKey = await repository.CreateAsync(providerKey, sid, roleKey!.Value);

        try
        {
            var all = await repository.GetAllAsync();
            var created = Assert.Single(all, m => m.MappingKey == mappingKey);
            Assert.Equal(sid, created.IdentityGroupName);
            Assert.Equal("Viewer", created.RoleName);
        }
        finally
        {
            await repository.DeleteAsync(mappingKey);
        }

        var afterDelete = await repository.GetAllAsync();
        Assert.DoesNotContain(afterDelete, m => m.MappingKey == mappingKey);
    }

    private static async Task<int> GetWindowsIntegratedProviderKeyAsync()
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.ConnectionString);
        return await Dapper.SqlMapper.QuerySingleAsync<int>(connection,
            "SELECT ProviderKey FROM web.identity_provider_config WHERE ProviderType = 'WindowsIntegrated'");
    }
}
