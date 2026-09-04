using BlueTrack.Api.Data;
using BlueTrack.Api.Secrets;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// Exercises the real Claims Normalization Pipeline query
/// (AuthorizationRepository) against the DevFakeAuth role/permission matrix
/// seeded by Database/Test/01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql --
/// proves that seed data and this repository's SQL actually agree with
/// each other, not just that each looks right in isolation.
/// </summary>
public class AuthorizationRepositoryTests
{
    private static async Task<int> GetDevFakeAuthProviderKeyAsync()
    {
        var identityProviderRepository = new IdentityProviderRepository(new TestDbConnectionFactory(), new WindowsDpapiProtector());
        var provider = await identityProviderRepository.GetByTypeAsync("DevFakeAuth");
        Assert.NotNull(provider);
        Assert.True(provider!.IsEnabled, "DevFakeAuth must be enabled in BlueTrackTest -- re-run Database/Test/01_BlueTrack_Test_DevFakeAuthMatrixSeed.sql.");
        return provider.ProviderKey;
    }

    [Fact]
    public async Task GetMatchedRoleNamesAsync_TestUserViewer_ResolvesToViewerRole()
    {
        var providerKey = await GetDevFakeAuthProviderKeyAsync();
        var repository = new AuthorizationRepository(new TestDbConnectionFactory());

        var roles = await repository.GetMatchedRoleNamesAsync(providerKey, ["TestUser.Viewer"]);

        Assert.Equal(["Viewer"], roles);
    }

    [Fact]
    public async Task GetEffectivePermissionNamesAsync_TestUserViewer_HasOnlyReadPermissions()
    {
        var providerKey = await GetDevFakeAuthProviderKeyAsync();
        var repository = new AuthorizationRepository(new TestDbConnectionFactory());

        var permissions = await repository.GetEffectivePermissionNamesAsync(providerKey, ["TestUser.Viewer"]);

        Assert.Equal(new HashSet<string> { "ViewDashboard", "ViewAuditLog" }, permissions.ToHashSet());
    }

    [Fact]
    public async Task GetEffectivePermissionNamesAsync_TestUserApprover_IncludesApproveExceptions()
    {
        var providerKey = await GetDevFakeAuthProviderKeyAsync();
        var repository = new AuthorizationRepository(new TestDbConnectionFactory());

        var permissions = await repository.GetEffectivePermissionNamesAsync(providerKey, ["TestUser.Approver"]);

        Assert.Contains("ApproveExceptions", permissions);
        Assert.DoesNotContain("ManageRolesAndPermissions", permissions);
    }

    [Fact]
    public async Task GetEffectivePermissionNamesAsync_TestUserAdmin_HasEveryConfirmedPermission()
    {
        var providerKey = await GetDevFakeAuthProviderKeyAsync();
        var repository = new AuthorizationRepository(new TestDbConnectionFactory());

        var permissions = await repository.GetEffectivePermissionNamesAsync(providerKey, ["TestUser.Admin"]);

        Assert.Contains("ManageRolesAndPermissions", permissions);
        Assert.Contains("ManageSecretsStore", permissions);
        Assert.True(permissions.Count >= 14, $"Expected every confirmed permission (D-61); got {permissions.Count}.");
    }

    [Fact]
    public async Task GetEffectivePermissionNamesAsync_UnknownIdentity_ReturnsEmpty()
    {
        var providerKey = await GetDevFakeAuthProviderKeyAsync();
        var repository = new AuthorizationRepository(new TestDbConnectionFactory());

        var permissions = await repository.GetEffectivePermissionNamesAsync(providerKey, ["TestUser.DoesNotExist"]);

        Assert.Empty(permissions);
    }
}
