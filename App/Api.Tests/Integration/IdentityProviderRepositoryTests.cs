using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.Secrets;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// web.identity_provider_config backs the login-provider list and the
/// Identity Providers admin page. Uses the real WindowsDpapiProtector
/// (not a fake) since D-84's whole point is that SecretReference is a
/// real DPAPI blob, not a plaintext passthrough -- this dev host is
/// Windows, so DataProtectionScope.LocalMachine works the same as it does
/// in the real app pool.
/// </summary>
public class IdentityProviderRepositoryTests
{
    private static IdentityProviderRepository CreateRepository() => new(new TestDbConnectionFactory(), new WindowsDpapiProtector());

    [Fact]
    public async Task GetByTypeAsync_KnownProvider_ReturnsIt()
    {
        var repository = CreateRepository();

        var provider = await repository.GetByTypeAsync("DevFakeAuth");

        Assert.NotNull(provider);
        Assert.Equal("DevFakeAuth", provider!.ProviderType);
    }

    [Fact]
    public async Task GetByTypeAsync_UnknownProvider_ReturnsNull()
    {
        var repository = CreateRepository();

        var provider = await repository.GetByTypeAsync("NoSuchProvider_IntegrationTest_9f8e7d");

        Assert.Null(provider);
    }

    [Fact]
    public async Task GetEnabledAsync_OnlyReturnsEnabledProviders()
    {
        var repository = CreateRepository();

        var enabled = await repository.GetEnabledAsync();

        Assert.All(enabled, p => Assert.True(p.IsEnabled));
        Assert.Contains(enabled, p => p.ProviderType == "DevFakeAuth");
    }

    [Fact]
    public async Task CreateAsync_WithPlaintextSecret_IsStoredProtectedAndRedactedInGetAllAsync()
    {
        var repository = CreateRepository();
        var providerType = $"IntegrationTest_{Guid.NewGuid():N}";

        var providerKey = await repository.CreateAsync(new SaveIdentityProviderRequest
        {
            ProviderType = providerType,
            DisplayName = "Integration Test Provider",
            IsEnabled = false,
            DisplayOrder = 999,
            PlaintextSecret = "correct-horse-battery-staple"
        });

        try
        {
            var redacted = await repository.GetAllAsync();
            var created = Assert.Single(redacted, p => p.ProviderKey == providerKey);
            Assert.Equal("***", created.SecretReference);

            // GetByTypeAsync is IdentityProviderConfig (the login-flow shape),
            // which never carries SecretReference at all -- confirm the
            // protected value directly via a raw row so this test still
            // proves the plaintext was never persisted verbatim.
            var protectedValue = await GetRawSecretReferenceAsync(providerKey);
            Assert.NotNull(protectedValue);
            Assert.NotEqual("correct-horse-battery-staple", protectedValue);
            Assert.Equal("correct-horse-battery-staple", new WindowsDpapiProtector().Unprotect(protectedValue!));
        }
        finally
        {
            await repository.DeleteAsync(providerKey);
        }
    }

    [Fact]
    public async Task UpdateAsync_WithoutPlaintextSecret_LeavesExistingSecretReferenceUnchanged()
    {
        var repository = CreateRepository();
        var providerType = $"IntegrationTest_{Guid.NewGuid():N}";
        var providerKey = await repository.CreateAsync(new SaveIdentityProviderRequest
        {
            ProviderType = providerType,
            DisplayName = "Original Display Name",
            IsEnabled = false,
            DisplayOrder = 999,
            PlaintextSecret = "original-secret"
        });
        var originalProtectedValue = await GetRawSecretReferenceAsync(providerKey);

        try
        {
            await repository.UpdateAsync(providerKey, new SaveIdentityProviderRequest
            {
                ProviderType = providerType,
                DisplayName = "Updated Display Name",
                IsEnabled = true,
                DisplayOrder = 998
            });

            var afterUpdate = await repository.GetAllAsync();
            var updated = Assert.Single(afterUpdate, p => p.ProviderKey == providerKey);
            Assert.Equal("Updated Display Name", updated.DisplayName);
            Assert.True(updated.IsEnabled);

            var protectedValueAfterUpdate = await GetRawSecretReferenceAsync(providerKey);
            Assert.Equal(originalProtectedValue, protectedValueAfterUpdate);
        }
        finally
        {
            await repository.DeleteAsync(providerKey);
        }
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheRow()
    {
        var repository = CreateRepository();
        var providerType = $"IntegrationTest_{Guid.NewGuid():N}";
        var providerKey = await repository.CreateAsync(new SaveIdentityProviderRequest
        {
            ProviderType = providerType,
            DisplayName = "To Be Deleted",
            IsEnabled = false,
            DisplayOrder = 999
        });

        await repository.DeleteAsync(providerKey);

        var all = await repository.GetAllAsync();
        Assert.DoesNotContain(all, p => p.ProviderKey == providerKey);
    }

    private static async Task<string?> GetRawSecretReferenceAsync(int providerKey)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.ConnectionString);
        return await Dapper.SqlMapper.QuerySingleAsync<string?>(connection,
            "SELECT SecretReference FROM web.identity_provider_config WHERE ProviderKey = @ProviderKey", new { ProviderKey = providerKey });
    }
}
