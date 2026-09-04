using BlueTrack.Api.Data;
using BlueTrack.Api.Models;
using BlueTrack.Api.Secrets;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// web.secrets_store (Design_Secrets_Storage.md) is a fixed, pre-seeded set
/// of six backend rows (14_BlueTrack_SecretsStoreSchema.sql) -- there's no
/// Create/Delete, only SetActiveAsync, and exactly one row is active at a
/// time. WindowsDpapi is seeded active by default, so every test here
/// restores that afterward since this is shared, real BlueTrackTest state.
/// </summary>
public class SecretsStoreRepositoryTests
{
    private static SecretsStoreRepository CreateRepository() => new(new TestDbConnectionFactory(), new WindowsDpapiProtector());

    [Fact]
    public async Task GetAllAsync_ReturnsAllSixSeededBackends()
    {
        var repository = CreateRepository();

        var backends = await repository.GetAllAsync();

        Assert.Equal(6, backends.Count);
        Assert.Contains(backends, b => b.BackendType == "WindowsDpapi");
        Assert.Contains(backends, b => b.BackendType == "CyberArkCP");
    }

    [Fact]
    public async Task GetByTypeAsync_UnknownType_ReturnsNull()
    {
        var repository = CreateRepository();

        var backend = await repository.GetByTypeAsync("NoSuchBackend_IntegrationTest_9f8e7d");

        Assert.Null(backend);
    }

    [Fact]
    public async Task SetActiveAsync_WithPlaintextCredential_ProtectsItAndDeactivatesTheOthers()
    {
        var repository = CreateRepository();

        try
        {
            await repository.SetActiveAsync(new SetActiveSecretsStoreRequest
            {
                BackendType = "AzureKeyVault",
                BackendSettings = """{"VaultUri":"https://integration-test.vault.azure.net/"}""",
                PlaintextCredential = "integration-test-client-secret"
            });

            var all = await repository.GetAllAsync();
            var azure = Assert.Single(all, b => b.BackendType == "AzureKeyVault");
            Assert.True(azure.IsActive);
            Assert.Contains("\"ProtectedCredential\":\"***\"", azure.BackendSettings);
            Assert.DoesNotContain("integration-test-client-secret", azure.BackendSettings);

            var dpapi = Assert.Single(all, b => b.BackendType == "WindowsDpapi");
            Assert.False(dpapi.IsActive);

            // Confirm the real credential round-trips through the protector --
            // GetByTypeAsync is the unredacted path providers use to authenticate.
            var unredactedAzure = await repository.GetByTypeAsync("AzureKeyVault");
            var settingsNode = System.Text.Json.Nodes.JsonNode.Parse(unredactedAzure!.BackendSettings!)!.AsObject();
            var protectedCredential = settingsNode["ProtectedCredential"]!.GetValue<string>();
            Assert.Equal("integration-test-client-secret", new WindowsDpapiProtector().Unprotect(protectedCredential));
        }
        finally
        {
            await repository.SetActiveAsync(new SetActiveSecretsStoreRequest { BackendType = "WindowsDpapi", BackendSettings = null });
        }
    }

    [Fact]
    public async Task SetActiveAsync_WithoutPlaintextCredential_ActivatesWithPlainBackendSettings()
    {
        var repository = CreateRepository();

        try
        {
            await repository.SetActiveAsync(new SetActiveSecretsStoreRequest
            {
                BackendType = "CyberArkCP",
                BackendSettings = """{"CcpUrl":"https://integration-test.example/"}"""
            });

            var backend = await repository.GetByTypeAsync("CyberArkCP");
            Assert.NotNull(backend);
            Assert.True(backend!.IsActive);
            Assert.Equal("""{"CcpUrl":"https://integration-test.example/"}""", backend.BackendSettings);
        }
        finally
        {
            await repository.SetActiveAsync(new SetActiveSecretsStoreRequest { BackendType = "WindowsDpapi", BackendSettings = null });
        }
    }
}
