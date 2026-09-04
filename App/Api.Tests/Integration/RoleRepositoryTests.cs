using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>web.app_role/web.role_permission back the Roles & Permissions admin page. The permission catalog itself (web.app_permission) is fixed (D-05/D-61), not editable here.</summary>
public class RoleRepositoryTests
{
    private static RoleRepository CreateRepository() => new(new TestDbConnectionFactory());

    [Fact]
    public async Task GetPermissionCatalogAsync_IncludesAKnownPermission()
    {
        var repository = CreateRepository();

        var catalog = await repository.GetPermissionCatalogAsync();

        Assert.Contains(catalog, p => p.PermissionName == "ViewDashboard");
    }

    [Fact]
    public async Task CreateRoleAsync_IsReadableByGetRolesAsyncWithItsPermissionBundle()
    {
        var repository = CreateRepository();
        var roleName = $"IntegrationTestRole_{Guid.NewGuid():N}";

        var roleKey = await repository.CreateRoleAsync(new SaveRoleRequest
        {
            RoleName = roleName,
            Description = "Created by an integration test",
            PermissionNames = ["ViewDashboard", "ViewAuditLog"]
        });

        try
        {
            var roles = await repository.GetRolesAsync();
            var created = Assert.Single(roles, r => r.AppRoleKey == roleKey);
            Assert.Equal("Created by an integration test", created.Description);
            Assert.Equal(2, created.PermissionNames.Count);
            Assert.Contains("ViewDashboard", created.PermissionNames);
            Assert.Contains("ViewAuditLog", created.PermissionNames);
        }
        finally
        {
            await repository.DeleteRoleAsync(roleKey);
        }
    }

    [Fact]
    public async Task UpdateRoleAsync_ReplacesThePermissionBundleEntirely()
    {
        var repository = CreateRepository();
        var roleKey = await repository.CreateRoleAsync(new SaveRoleRequest
        {
            RoleName = $"IntegrationTestRole_{Guid.NewGuid():N}",
            PermissionNames = ["ViewDashboard", "ViewAuditLog"]
        });

        try
        {
            await repository.UpdateRoleAsync(roleKey, new SaveRoleRequest
            {
                RoleName = $"IntegrationTestRole_{Guid.NewGuid():N}_Renamed",
                Description = "Now has a description",
                PermissionNames = ["ViewDashboard"]
            });

            var roles = await repository.GetRolesAsync();
            var updated = Assert.Single(roles, r => r.AppRoleKey == roleKey);
            Assert.Equal("Now has a description", updated.Description);
            Assert.Single(updated.PermissionNames);
            Assert.Contains("ViewDashboard", updated.PermissionNames);
            Assert.DoesNotContain("ViewAuditLog", updated.PermissionNames);
        }
        finally
        {
            await repository.DeleteRoleAsync(roleKey);
        }
    }

    [Fact]
    public async Task CreateRoleAsync_WithNoPermissions_CreatesARoleWithAnEmptyBundle()
    {
        var repository = CreateRepository();

        var roleKey = await repository.CreateRoleAsync(new SaveRoleRequest
        {
            RoleName = $"IntegrationTestRole_{Guid.NewGuid():N}",
            PermissionNames = []
        });

        try
        {
            var roles = await repository.GetRolesAsync();
            var created = Assert.Single(roles, r => r.AppRoleKey == roleKey);
            Assert.Empty(created.PermissionNames);
        }
        finally
        {
            await repository.DeleteRoleAsync(roleKey);
        }
    }

    [Fact]
    public async Task DeleteRoleAsync_RemovesTheRoleAndItsPermissionBundle()
    {
        var repository = CreateRepository();
        var roleKey = await repository.CreateRoleAsync(new SaveRoleRequest
        {
            RoleName = $"IntegrationTestRole_{Guid.NewGuid():N}",
            PermissionNames = ["ViewDashboard"]
        });

        await repository.DeleteRoleAsync(roleKey);

        var roles = await repository.GetRolesAsync();
        Assert.DoesNotContain(roles, r => r.AppRoleKey == roleKey);
    }
}
