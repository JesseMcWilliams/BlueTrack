using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.Secrets;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// web.ldap_config (D-116/D-118, extended to a real multi-row table 2026-09-16
/// for the AD Account Discovery feature). Each test creates its own
/// uniquely-named domain row and deletes it afterward, rather than mutating
/// the seeded 'Default' row -- unlike the pre-D-58-restructure singleton
/// version of this file, tests no longer need to save/restore shared state.
/// </summary>
public class LdapConfigRepositoryTests
{
    private static LdapConfigRepository CreateRepository() => new(new TestDbConnectionFactory());

    [Fact]
    public async Task CreateAsync_ThenGetByKeyAsync_RoundTripsAllFields_IncludingUseTrustedConnection()
    {
        var repository = CreateRepository();
        var domainName = $"IntegrationTest_{Guid.NewGuid():N}";

        var key = await repository.CreateAsync(new SaveLdapConfigRequest
        {
            DomainName = domainName,
            IsEnabled = true,
            DomainController = "dc01.integrationtest.local",
            SearchBase = "DC=integrationtest,DC=local",
            UseSsl = true,
            UseTrustedConnection = true,
            CredentialKey = null
        }, createdByUserKey: null);

        try
        {
            var reloaded = await repository.GetByKeyAsync(key);
            Assert.NotNull(reloaded);
            Assert.Equal(domainName, reloaded!.DomainName);
            Assert.True(reloaded.IsEnabled);
            Assert.Equal("dc01.integrationtest.local", reloaded.DomainController);
            Assert.Equal("DC=integrationtest,DC=local", reloaded.SearchBase);
            Assert.True(reloaded.UseSsl);
            Assert.True(reloaded.UseTrustedConnection);
            Assert.Null(reloaded.CredentialKey);
        }
        finally
        {
            await repository.DeleteAsync(key);
        }
    }

    [Fact]
    public async Task UpdateAsync_PersistsChanges_ReadableViaGetByKeyAsync()
    {
        var repository = CreateRepository();
        var domainName = $"IntegrationTest_{Guid.NewGuid():N}";
        var key = await repository.CreateAsync(new SaveLdapConfigRequest
        {
            DomainName = domainName,
            IsEnabled = false,
            DomainController = null,
            SearchBase = null,
            UseSsl = false,
            UseTrustedConnection = true,
            CredentialKey = null
        }, createdByUserKey: null);

        try
        {
            await repository.UpdateAsync(key, new SaveLdapConfigRequest
            {
                DomainName = domainName,
                IsEnabled = true,
                DomainController = "dc02.integrationtest.local",
                SearchBase = "DC=integrationtest,DC=local",
                UseSsl = false,
                UseTrustedConnection = true,
                CredentialKey = null
            }, modifiedByUserKey: null);

            var reloaded = await repository.GetByKeyAsync(key);
            Assert.NotNull(reloaded);
            Assert.True(reloaded!.IsEnabled);
            Assert.Equal("dc02.integrationtest.local", reloaded.DomainController);
        }
        finally
        {
            await repository.DeleteAsync(key);
        }
    }

    [Fact]
    public async Task CreateAsync_WithCredentialKey_JoinsCredentialNameOnGetByKeyAsync()
    {
        var repository = CreateRepository();
        var protector = new WindowsDpapiProtector();
        var credentialRepository = new CredentialRepository(
            new TestDbConnectionFactory(),
            protector,
            new VaultSecretProviderResolver([], new SecretsStoreRepository(new TestDbConnectionFactory(), protector)),
            new DpapiCredentialResolver(new TestDbConnectionFactory(), protector));
        var credentialName = $"IntegrationTestLdapBind_{Guid.NewGuid():N}";
        var credentialKey = await credentialRepository.CreateAsync(new SaveCredentialRequest
        {
            CredentialName = credentialName,
            BackendType = "WindowsDpapi",
            Username = "ldap-bind-user",
            PlaintextPassword = "ldap-bind-password"
        }, modifiedByUserKey: null);

        var domainName = $"IntegrationTest_{Guid.NewGuid():N}";
        var key = await repository.CreateAsync(new SaveLdapConfigRequest
        {
            DomainName = domainName,
            IsEnabled = false,
            DomainController = null,
            SearchBase = null,
            UseSsl = false,
            UseTrustedConnection = false,
            CredentialKey = credentialKey
        }, createdByUserKey: null);

        try
        {
            var reloaded = await repository.GetByKeyAsync(key);
            Assert.NotNull(reloaded);
            Assert.Equal(credentialKey, reloaded!.CredentialKey);
            Assert.Equal(credentialName, reloaded.CredentialName);
        }
        finally
        {
            await repository.DeleteAsync(key);
            await credentialRepository.DeleteAsync(credentialKey);
        }
    }

    [Fact]
    public async Task GetEnabledAsync_OnlyReturnsEnabledRows()
    {
        var repository = CreateRepository();
        var enabledDomain = $"IntegrationTestEnabled_{Guid.NewGuid():N}";
        var disabledDomain = $"IntegrationTestDisabled_{Guid.NewGuid():N}";
        var enabledKey = await repository.CreateAsync(new SaveLdapConfigRequest
        {
            DomainName = enabledDomain,
            IsEnabled = true,
            UseTrustedConnection = true
        }, createdByUserKey: null);
        var disabledKey = await repository.CreateAsync(new SaveLdapConfigRequest
        {
            DomainName = disabledDomain,
            IsEnabled = false,
            UseTrustedConnection = true
        }, createdByUserKey: null);

        try
        {
            var enabled = await repository.GetEnabledAsync();
            Assert.Contains(enabled, c => c.DomainName == enabledDomain);
            Assert.DoesNotContain(enabled, c => c.DomainName == disabledDomain);
        }
        finally
        {
            await repository.DeleteAsync(enabledKey);
            await repository.DeleteAsync(disabledKey);
        }
    }
}
