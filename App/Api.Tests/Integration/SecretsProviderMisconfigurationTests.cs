using BlueTrack.Api.Data;
using BlueTrack.Api.Secrets;
using Dapper;
using Microsoft.Data.SqlClient;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// Design_Testing_Strategy.md explicitly calls for one thing to be tested
/// on each of the new placeholder secrets providers even though a real
/// vault isn't reachable: "each provider throws a correctly-classified
/// SecretRetrievalException when misconfigured." Every branch tested here
/// is a pure local check (empty settings / malformed JSON / missing
/// required fields) that throws before any local SDK call or network
/// request is ever made -- confirmed by reading each provider's source.
/// Live-call behavior against a real vault stays out of scope per that
/// same document.
///
/// GetByTypeAsync doesn't filter by IsActive, so these set a backend's
/// BackendSettings directly via SQL without touching which backend is
/// actually active -- no interaction with the WindowsDpapi-active
/// invariant other tests (SecretsStoreRepositoryTests,
/// AdminControllersFunctionalTests) rely on.
/// </summary>
public class SecretsProviderMisconfigurationTests
{
    [Fact]
    public async Task CyberArkCp_EmptyBackendSettings_ThrowsNotConfigured()
    {
        await SetBackendSettingsAsync("CyberArkCP", null);
        var provider = new CyberArkCpSecretsProvider(new SecretsStoreRepository(new TestDbConnectionFactory(), new WindowsDpapiProtector()));

        var ex = await Assert.ThrowsAsync<SecretRetrievalException>(() => provider.GetSecretAsync(new SecretQuery("Safe", "Root", "Object")));

        Assert.Contains("not configured", ex.Message);
        Assert.Equal(CyberArkErrorCategory.Other, ex.Category);
    }

    [Fact]
    public async Task CyberArkCp_MalformedJson_ThrowsWithClearMessage()
    {
        await SetBackendSettingsAsync("CyberArkCP", "{not valid json");
        var provider = new CyberArkCpSecretsProvider(new SecretsStoreRepository(new TestDbConnectionFactory(), new WindowsDpapiProtector()));

        var ex = await Assert.ThrowsAsync<SecretRetrievalException>(() => provider.GetSecretAsync(new SecretQuery("Safe", "Root", "Object")));

        Assert.Contains("could not be parsed as JSON", ex.Message);

        await SetBackendSettingsAsync("CyberArkCP", null);
    }

    [Fact]
    public async Task CyberArkCcp_EmptyBackendSettings_ThrowsNotConfigured()
    {
        await SetBackendSettingsAsync("CyberArkCCP", null);
        var provider = new CyberArkCcpSecretsProvider(new NoOpHttpClientFactory(), new SecretsStoreRepository(new TestDbConnectionFactory(), new WindowsDpapiProtector()));

        var ex = await Assert.ThrowsAsync<SecretRetrievalException>(() => provider.GetSecretAsync(new SecretQuery("Safe", "Root", "Object")));

        Assert.Contains("not configured", ex.Message);
        Assert.Equal(CyberArkErrorCategory.Other, ex.Category);
    }

    [Fact]
    public async Task CyberArkCcp_MalformedJson_ThrowsWithClearMessage()
    {
        await SetBackendSettingsAsync("CyberArkCCP", "{not valid json");
        var provider = new CyberArkCcpSecretsProvider(new NoOpHttpClientFactory(), new SecretsStoreRepository(new TestDbConnectionFactory(), new WindowsDpapiProtector()));

        var ex = await Assert.ThrowsAsync<SecretRetrievalException>(() => provider.GetSecretAsync(new SecretQuery("Safe", "Root", "Object")));

        Assert.Contains("could not be parsed as JSON", ex.Message);

        await SetBackendSettingsAsync("CyberArkCCP", null);
    }

    [Fact]
    public async Task CyberArkConjur_EmptyBackendSettings_ThrowsNotConfigured()
    {
        await SetBackendSettingsAsync("CyberArkConjur", null);
        var provider = CreateConjurProvider();

        var ex = await Assert.ThrowsAsync<SecretRetrievalException>(() => provider.GetSecretAsync(new SecretQuery("", "", "variable/id")));

        Assert.Contains("not configured", ex.Message);
    }

    [Fact]
    public async Task CyberArkConjur_MissingRequiredFields_ThrowsWithClearMessage()
    {
        await SetBackendSettingsAsync("CyberArkConjur", """{"ApplianceUrl":"https://conjur.example/"}""");
        var provider = CreateConjurProvider();

        var ex = await Assert.ThrowsAsync<SecretRetrievalException>(() => provider.GetSecretAsync(new SecretQuery("", "", "variable/id")));

        Assert.Contains("ApplianceUrl, Account, and Login are all required", ex.Message);

        await SetBackendSettingsAsync("CyberArkConjur", null);
    }

    [Fact]
    public async Task CyberArkConjur_MissingStoredApiKey_ThrowsWithClearMessage()
    {
        await SetBackendSettingsAsync("CyberArkConjur", """{"ApplianceUrl":"https://conjur.example/","Account":"myaccount","Login":"host/myapp"}""");
        var provider = CreateConjurProvider();

        var ex = await Assert.ThrowsAsync<SecretRetrievalException>(() => provider.GetSecretAsync(new SecretQuery("", "", "variable/id")));

        Assert.Contains("no stored API key configured", ex.Message);

        await SetBackendSettingsAsync("CyberArkConjur", null);
    }

    [Fact]
    public async Task AzureKeyVault_EmptyBackendSettings_ThrowsNotConfigured()
    {
        await SetBackendSettingsAsync("AzureKeyVault", null);
        var provider = new AzureKeyVaultSecretsProvider(new SecretsStoreRepository(new TestDbConnectionFactory(), new WindowsDpapiProtector()), new WindowsDpapiProtector());

        var ex = await Assert.ThrowsAsync<SecretRetrievalException>(() => provider.GetSecretAsync(new SecretQuery("", "", "my-secret")));

        Assert.Contains("not configured", ex.Message);
    }

    [Fact]
    public async Task AzureKeyVault_EmptyVaultUri_ThrowsWithClearMessage()
    {
        await SetBackendSettingsAsync("AzureKeyVault", """{"VaultUri":""}""");
        var provider = new AzureKeyVaultSecretsProvider(new SecretsStoreRepository(new TestDbConnectionFactory(), new WindowsDpapiProtector()), new WindowsDpapiProtector());

        var ex = await Assert.ThrowsAsync<SecretRetrievalException>(() => provider.GetSecretAsync(new SecretQuery("", "", "my-secret")));

        Assert.Contains("VaultUri is empty", ex.Message);

        await SetBackendSettingsAsync("AzureKeyVault", null);
    }

    [Fact]
    public async Task AzureKeyVault_ServicePrincipalMissingFields_ThrowsWithClearMessage()
    {
        await SetBackendSettingsAsync("AzureKeyVault", """{"VaultUri":"https://fake.vault.azure.net/","AuthMethod":"ServicePrincipal"}""");
        var provider = new AzureKeyVaultSecretsProvider(new SecretsStoreRepository(new TestDbConnectionFactory(), new WindowsDpapiProtector()), new WindowsDpapiProtector());

        var ex = await Assert.ThrowsAsync<SecretRetrievalException>(() => provider.GetSecretAsync(new SecretQuery("", "", "my-secret")));

        Assert.Contains("requires TenantId, ClientId, and a stored client secret", ex.Message);

        await SetBackendSettingsAsync("AzureKeyVault", null);
    }

    [Fact]
    public async Task AwsSecretsManager_EmptyBackendSettings_ThrowsNotConfigured()
    {
        await SetBackendSettingsAsync("AwsSecretsManager", null);
        var provider = new AwsSecretsManagerSecretsProvider(new SecretsStoreRepository(new TestDbConnectionFactory(), new WindowsDpapiProtector()), new WindowsDpapiProtector());

        var ex = await Assert.ThrowsAsync<SecretRetrievalException>(() => provider.GetSecretAsync(new SecretQuery("", "", "my-secret")));

        Assert.Contains("not configured", ex.Message);
    }

    [Fact]
    public async Task AwsSecretsManager_EmptyRegion_ThrowsWithClearMessage()
    {
        await SetBackendSettingsAsync("AwsSecretsManager", """{"Region":""}""");
        var provider = new AwsSecretsManagerSecretsProvider(new SecretsStoreRepository(new TestDbConnectionFactory(), new WindowsDpapiProtector()), new WindowsDpapiProtector());

        var ex = await Assert.ThrowsAsync<SecretRetrievalException>(() => provider.GetSecretAsync(new SecretQuery("", "", "my-secret")));

        Assert.Contains("Region is empty", ex.Message);

        await SetBackendSettingsAsync("AwsSecretsManager", null);
    }

    [Fact]
    public async Task AwsSecretsManager_AccessKeyMissingFields_ThrowsWithClearMessage()
    {
        await SetBackendSettingsAsync("AwsSecretsManager", """{"Region":"us-east-1","AuthMethod":"AccessKey"}""");
        var provider = new AwsSecretsManagerSecretsProvider(new SecretsStoreRepository(new TestDbConnectionFactory(), new WindowsDpapiProtector()), new WindowsDpapiProtector());

        var ex = await Assert.ThrowsAsync<SecretRetrievalException>(() => provider.GetSecretAsync(new SecretQuery("", "", "my-secret")));

        Assert.Contains("requires AccessKeyId and a stored secret access key", ex.Message);

        await SetBackendSettingsAsync("AwsSecretsManager", null);
    }

    private static CyberArkConjurSecretsProvider CreateConjurProvider() =>
        new(new NoOpHttpClientFactory(), new SecretsStoreRepository(new TestDbConnectionFactory(), new WindowsDpapiProtector()), new WindowsDpapiProtector());

    private static async Task SetBackendSettingsAsync(string backendType, string? backendSettings)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync(
            "UPDATE web.secrets_store SET BackendSettings = @BackendSettings WHERE BackendType = @BackendType",
            new { BackendType = backendType, BackendSettings = backendSettings });
    }

    /// <summary>
    /// Every branch under test throws before any HttpClient call is made --
    /// this exists only to satisfy the constructor; calling CreateClient on
    /// it would be a real test bug, not something these tests should need.
    /// </summary>
    private sealed class NoOpHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException(
            "Not expected to be called -- every test here throws on a local validation check before any HTTP call.");
    }
}
