using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.Secrets;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>web.ldap_config (D-116/D-118) -- singleton, restored to its original values after each test.</summary>
public class LdapConfigRepositoryTests
{
    private static LdapConfigRepository CreateRepository() => new(new TestDbConnectionFactory());

    [Fact]
    public async Task SaveAsync_ThenGetAsync_RoundTripsAllFields_IncludingUseTrustedConnection()
    {
        var repository = CreateRepository();
        var original = await repository.GetAsync();

        try
        {
            await repository.SaveAsync(new SaveLdapConfigRequest
            {
                IsEnabled = true,
                DomainController = "dc01.integrationtest.local",
                SearchBase = "DC=integrationtest,DC=local",
                UseSsl = true,
                UseTrustedConnection = true,
                CredentialKey = null
            }, modifiedByUserKey: null);

            var reloaded = await repository.GetAsync();
            Assert.True(reloaded.IsEnabled);
            Assert.Equal("dc01.integrationtest.local", reloaded.DomainController);
            Assert.Equal("DC=integrationtest,DC=local", reloaded.SearchBase);
            Assert.True(reloaded.UseSsl);
            Assert.True(reloaded.UseTrustedConnection);
            Assert.Null(reloaded.CredentialKey);
        }
        finally
        {
            await repository.SaveAsync(new SaveLdapConfigRequest
            {
                IsEnabled = original.IsEnabled,
                DomainController = original.DomainController,
                SearchBase = original.SearchBase,
                UseSsl = original.UseSsl,
                UseTrustedConnection = original.UseTrustedConnection,
                CredentialKey = original.CredentialKey
            }, modifiedByUserKey: null);
        }
    }

    [Fact]
    public async Task SaveAsync_WithCredentialKey_JoinsCredentialNameOnGetAsync()
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
        var original = await repository.GetAsync();

        try
        {
            await repository.SaveAsync(new SaveLdapConfigRequest
            {
                IsEnabled = false,
                DomainController = null,
                SearchBase = null,
                UseSsl = false,
                UseTrustedConnection = false,
                CredentialKey = credentialKey
            }, modifiedByUserKey: null);

            var reloaded = await repository.GetAsync();
            Assert.Equal(credentialKey, reloaded.CredentialKey);
            Assert.Equal(credentialName, reloaded.CredentialName);
        }
        finally
        {
            await repository.SaveAsync(new SaveLdapConfigRequest
            {
                IsEnabled = original.IsEnabled,
                DomainController = original.DomainController,
                SearchBase = original.SearchBase,
                UseSsl = original.UseSsl,
                UseTrustedConnection = original.UseTrustedConnection,
                CredentialKey = original.CredentialKey
            }, modifiedByUserKey: null);
            await credentialRepository.DeleteAsync(credentialKey);
        }
    }
}
