using BlueTrack.Api.Data;
using BlueTrack.Api.Secrets;
using BlueTrack.Api.Tests.Integration;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-310: a backend with no provider says why; for CyberArk CP, how to build it in.</summary>
public class VaultSecretProviderResolverTests
{
    // ResolveByBackendType never reads the database, so the repository is never used.
    private static VaultSecretProviderResolver ResolverWithNoProviders() =>
        new([], new SecretsStoreRepository(new TestDbConnectionFactory(), new WindowsDpapiProtector()));

    [Fact]
    public void CyberArkCp_NotBuiltIn_SaysToRerunTheBuildStep()
    {
        var ex = Assert.Throws<SecretRetrievalException>(() => ResolverWithNoProviders().ResolveByBackendType("CyberArkCP"));

        Assert.Contains("built without the CyberArk Application Password SDK", ex.Message);
        Assert.Contains("-Step Build.Api", ex.Message);
    }

    [Fact]
    public void AnotherBackend_WithNoProvider_SaysSo()
    {
        var ex = Assert.Throws<SecretRetrievalException>(() => ResolverWithNoProviders().ResolveByBackendType("SomethingElse"));

        Assert.Equal("'SomethingElse' has no provider implementation registered.", ex.Message);
    }
}
