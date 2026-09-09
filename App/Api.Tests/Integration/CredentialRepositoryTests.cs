using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.Secrets;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// web.credential (D-116/D-118) -- the named-credential store shared by
/// SMTP, LDAP, and WindowsDpapiVaultSecretsProvider. Uses the real
/// WindowsDpapiProtector, same reasoning as IdentityProviderRepositoryTests:
/// this dev host is Windows, so DataProtectionScope.Machine/CurrentUser
/// behave the same here as in the real app pool.
/// </summary>
public class CredentialRepositoryTests
{
    private static CredentialRepository CreateRepository()
    {
        var connectionFactory = new TestDbConnectionFactory();
        var protector = new WindowsDpapiProtector();
        var dpapiResolver = new DpapiCredentialResolver(connectionFactory, protector);
        var vaultResolver = new VaultSecretProviderResolver(
            [new WindowsDpapiVaultSecretsProvider(dpapiResolver)],
            new SecretsStoreRepository(connectionFactory, protector));
        return new CredentialRepository(connectionFactory, protector, vaultResolver, dpapiResolver);
    }

    [Fact]
    public async Task CreateAsync_DpapiWithMachineScope_ResolvesToPlaintext_AndStoresCiphertextNotPlaintext()
    {
        var repository = CreateRepository();
        var name = $"IntegrationTest_{Guid.NewGuid():N}";

        var credentialKey = await repository.CreateAsync(new SaveCredentialRequest
        {
            CredentialName = name,
            BackendType = "WindowsDpapi",
            Username = "integration-test-user",
            PlaintextPassword = "correct-horse-battery-staple",
            ScopePreference = "Machine"
        }, modifiedByUserKey: null);

        try
        {
            var redacted = await repository.GetAllAsync();
            var created = Assert.Single(redacted, c => c.CredentialKey == credentialKey);
            Assert.True(created.HasPassword);
            Assert.Equal("Machine", created.CurrentScope);

            var rawCiphertext = await GetRawProtectedPasswordAsync(credentialKey);
            Assert.NotNull(rawCiphertext);
            Assert.NotEqual("correct-horse-battery-staple", rawCiphertext);

            var (username, password) = await repository.ResolveForUseAsync(credentialKey);
            Assert.Equal("integration-test-user", username);
            Assert.Equal("correct-horse-battery-staple", password);
        }
        finally
        {
            await repository.DeleteAsync(credentialKey);
        }
    }

    [Fact]
    public async Task ResolveForUseAsync_ScopePreferenceUser_UpgradesFromMachineToUser_OnFirstResolve()
    {
        var repository = CreateRepository();
        var name = $"IntegrationTest_{Guid.NewGuid():N}";
        var credentialKey = await repository.CreateAsync(new SaveCredentialRequest
        {
            CredentialName = name,
            BackendType = "WindowsDpapi",
            Username = "scope-upgrade-user",
            PlaintextPassword = "scope-upgrade-password",
            ScopePreference = "User"
        }, modifiedByUserKey: null);

        try
        {
            var beforeResolve = await repository.GetAllAsync();
            Assert.Equal("Machine", Assert.Single(beforeResolve, c => c.CredentialKey == credentialKey).CurrentScope);
            var ciphertextBeforeResolve = await GetRawProtectedPasswordAsync(credentialKey);

            var (_, password) = await repository.ResolveForUseAsync(credentialKey);
            Assert.Equal("scope-upgrade-password", password);

            var afterResolve = await repository.GetAllAsync();
            Assert.Equal("User", Assert.Single(afterResolve, c => c.CredentialKey == credentialKey).CurrentScope);
            var ciphertextAfterResolve = await GetRawProtectedPasswordAsync(credentialKey);
            Assert.NotEqual(ciphertextBeforeResolve, ciphertextAfterResolve); // genuinely re-encrypted, not just relabeled

            // Still resolves correctly now that it's under CurrentUser scope.
            var (_, passwordAfterUpgrade) = await repository.ResolveForUseAsync(credentialKey);
            Assert.Equal("scope-upgrade-password", passwordAfterUpgrade);
        }
        finally
        {
            await repository.DeleteAsync(credentialKey);
        }
    }

    [Fact]
    public async Task UpdateAsync_WithoutPlaintextPassword_LeavesExistingPasswordUnchanged()
    {
        var repository = CreateRepository();
        var name = $"IntegrationTest_{Guid.NewGuid():N}";
        var credentialKey = await repository.CreateAsync(new SaveCredentialRequest
        {
            CredentialName = name,
            BackendType = "WindowsDpapi",
            Username = "original-user",
            PlaintextPassword = "original-password",
            ScopePreference = "Machine"
        }, modifiedByUserKey: null);
        var originalCiphertext = await GetRawProtectedPasswordAsync(credentialKey);

        try
        {
            await repository.UpdateAsync(credentialKey, new SaveCredentialRequest
            {
                CredentialName = name,
                BackendType = "WindowsDpapi",
                Username = "updated-user",
                ScopePreference = "Machine"
            }, modifiedByUserKey: null);

            var afterUpdate = await repository.GetAllAsync();
            var updated = Assert.Single(afterUpdate, c => c.CredentialKey == credentialKey);
            Assert.Equal("updated-user", updated.Username);

            Assert.Equal(originalCiphertext, await GetRawProtectedPasswordAsync(credentialKey));
            var (_, password) = await repository.ResolveForUseAsync(credentialKey);
            Assert.Equal("original-password", password);
        }
        finally
        {
            await repository.DeleteAsync(credentialKey);
        }
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheRow()
    {
        var repository = CreateRepository();
        var name = $"IntegrationTest_{Guid.NewGuid():N}";
        var credentialKey = await repository.CreateAsync(new SaveCredentialRequest
        {
            CredentialName = name,
            BackendType = "WindowsDpapi",
            PlaintextPassword = "to-be-deleted",
            ScopePreference = "Machine"
        }, modifiedByUserKey: null);

        await repository.DeleteAsync(credentialKey);

        var all = await repository.GetAllAsync();
        Assert.DoesNotContain(all, c => c.CredentialKey == credentialKey);
    }

    [Fact]
    public async Task ResolveForUseAsync_ViaWindowsDpapiVaultSecretsProvider_ResolvesByCredentialName()
    {
        // D-118: the same credential, reached the "vault" way (by name,
        // through IVaultSecretProvider) rather than by key -- confirms the
        // wrapper and CredentialRepository's own direct path agree.
        var repository = CreateRepository();
        var name = $"IntegrationTest_{Guid.NewGuid():N}";
        var credentialKey = await repository.CreateAsync(new SaveCredentialRequest
        {
            CredentialName = name,
            BackendType = "WindowsDpapi",
            Username = "vault-lookup-user",
            PlaintextPassword = "vault-lookup-password",
            ScopePreference = "Machine"
        }, modifiedByUserKey: null);

        try
        {
            var connectionFactory = new TestDbConnectionFactory();
            var protector = new WindowsDpapiProtector();
            var dpapiResolver = new DpapiCredentialResolver(connectionFactory, protector);
            var provider = new WindowsDpapiVaultSecretsProvider(dpapiResolver);

            var result = await provider.GetSecretAsync(new SecretQuery("ignored-safe", "ignored-folder", name));

            Assert.Equal("vault-lookup-user", result.UserName);
            Assert.Equal("vault-lookup-password", new string(result.Content));
        }
        finally
        {
            await repository.DeleteAsync(credentialKey);
        }
    }

    [Fact]
    public async Task ResolveForUseAsync_ViaWindowsDpapiVaultSecretsProvider_UnknownName_ThrowsNotFound()
    {
        var connectionFactory = new TestDbConnectionFactory();
        var protector = new WindowsDpapiProtector();
        var dpapiResolver = new DpapiCredentialResolver(connectionFactory, protector);
        var provider = new WindowsDpapiVaultSecretsProvider(dpapiResolver);

        var exception = await Assert.ThrowsAsync<SecretRetrievalException>(() =>
            provider.GetSecretAsync(new SecretQuery("safe", "folder", $"NoSuchCredential_{Guid.NewGuid():N}")));

        Assert.Equal(CyberArkErrorCategory.NotFound, exception.Category);
    }

    private static async Task<string?> GetRawProtectedPasswordAsync(int credentialKey)
    {
        await using var connection = new Microsoft.Data.SqlClient.SqlConnection(TestDatabase.ConnectionString);
        return await Dapper.SqlMapper.QuerySingleAsync<string?>(connection,
            "SELECT ProtectedPassword FROM web.credential WHERE CredentialKey = @CredentialKey", new { CredentialKey = credentialKey });
    }
}
