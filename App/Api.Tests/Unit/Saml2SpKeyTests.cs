using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BlueTrack.Api.Auth;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>
/// D-210: the SP key check behind the SAML sign-in 503 and the Identity
/// Providers health check. Certificates are made in memory, never added to
/// a store. An installed key the App Pool account can't read ("Keyset does
/// not exist") can't be made here; it was found live and is caught the same way.
/// </summary>
public class Saml2SpKeyTests
{
    private static X509Certificate2 RsaCertificate()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=BlueTrack SP key test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
    }

    [Fact]
    public void KeyProblem_UsableRsaKey_IsNull()
    {
        using var certificate = RsaCertificate();

        Assert.Null(Saml2ConfigurationFactory.KeyProblem(certificate));
    }

    [Fact]
    public void KeyProblem_CertificateWithoutPrivateKey_SaysSo()
    {
        using var withKey = RsaCertificate();
        using var publicOnly = X509CertificateLoader.LoadCertificate(withKey.Export(X509ContentType.Cert));

        Assert.Contains("no private key", Saml2ConfigurationFactory.KeyProblem(publicOnly));
    }

    [Fact]
    public void KeyProblem_NonRsaKey_SaysSo()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=BlueTrack SP key test", ecdsa, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));

        Assert.Contains("isn't an RSA key", Saml2ConfigurationFactory.KeyProblem(certificate));
    }

    [Fact]
    public void SpKeyProblem_ThumbprintNotInAnyStore_SaysItIsNotInstalled()
    {
        Assert.Contains("isn't installed", Saml2ConfigurationFactory.SpKeyProblem(new string('0', 40)));
    }
}
