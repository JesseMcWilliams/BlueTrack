using System.Security.Cryptography;
using System.Text;
using BlueTrack.Api.Secrets;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-215: the secrets backends' stored credentials (User-scope DPAPI, random entropy) and Conjur's token header.</summary>
public class StoredCredentialProtectionTests
{
    private readonly WindowsDpapiProtector protector = new();

    [Fact]
    public void ProtectStored_RoundTrips_AtUserScope_WithFreshEntropyEachTime()
    {
        var first = protector.ProtectStored("ThisIsMy_FAKE_Password6!");
        var second = protector.ProtectStored("ThisIsMy_FAKE_Password6!");

        Assert.StartsWith("dpapi:user:v1:", first);   // the test runs with a loaded user profile
        Assert.NotEqual(first, second);
        Assert.Equal("ThisIsMy_FAKE_Password6!", protector.UnprotectStored(first));
        Assert.Equal("ThisIsMy_FAKE_Password6!", protector.UnprotectStored(second));
    }

    [Fact]
    public void UnprotectStored_StillReadsAnOlderMachineScopeValue()
    {
        var older = protector.Protect("ThisIsMy_FAKE_Password6!");

        Assert.Equal("ThisIsMy_FAKE_Password6!", protector.UnprotectStored(older));
    }

    [Fact]
    public void UnprotectStored_RefusesAnUnknownFormat()
    {
        Assert.Throws<CryptographicException>(() => protector.UnprotectStored("dpapi:user:v9:AAAA:AAAA"));
    }

    [Fact]
    public void ConjurAuthorizationHeader_IsTheBase64OfTheRawToken()
    {
        const string raw = "{\"protected\":\"eyJhbGciOiJjb25qdXIub3JnL3Nsb3NpbG8vdjIifQ==\",\"payload\":\"e30=\",\"signature\":\"c2ln\"}";

        var header = CyberArkConjurSecretsProvider.AuthorizationHeaderValue(raw);

        Assert.Equal($"Token token=\"{Convert.ToBase64String(Encoding.UTF8.GetBytes(raw))}\"", header);
    }
}
