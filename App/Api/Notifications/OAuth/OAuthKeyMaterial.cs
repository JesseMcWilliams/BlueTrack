using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BlueTrack.Api.Notifications.OAuth;

/// <summary>D-223: a Google service account key file's fields that the JWT bearer flow uses.</summary>
public sealed record GoogleServiceAccountKey(string ClientEmail, string PrivateKeyPem, string? PrivateKeyId);

/// <summary>D-223: the certificate (DER) and RSA private key Microsoft's certificate client assertion is signed with.</summary>
public sealed record CertificateKeyPair(byte[] CertificateDer, RSA PrivateKey);

/// <summary>
/// D-223: reads the key material an OAuth2 SMTP credential holds. The
/// Credentials page stores it as the credential's password, and its
/// password box is a single-line input, so line breaks may have been
/// stripped: PEM is read tolerantly (any whitespace, or none, inside a
/// block). Messages never include the material itself.
/// </summary>
public static partial class OAuthKeyMaterial
{
    public static GoogleServiceAccountKey ReadGoogleServiceAccountKey(string json)
    {
        const string notAKey = "The credential's password isn't a Google service account JSON key (expected \"type\": \"service_account\" with client_email and private_key). Paste the whole key file into the credential.";
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            string? Read(string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

            var type = Read("type");
            var clientEmail = Read("client_email");
            var privateKey = Read("private_key");
            if (type != "service_account" || string.IsNullOrWhiteSpace(clientEmail) || string.IsNullOrWhiteSpace(privateKey))
            {
                throw new OAuthTokenException(notAKey);
            }
            return new GoogleServiceAccountKey(clientEmail, privateKey, Read("private_key_id"));
        }
        catch (JsonException)
        {
            throw new OAuthTokenException(notAKey);
        }
    }

    /// <summary>An RSA key from a PEM "PRIVATE KEY" (PKCS#8) or "RSA PRIVATE KEY" (PKCS#1) block.</summary>
    public static RSA ReadRsaPrivateKey(string pem)
    {
        foreach (var (label, der) in ReadPemBlocks(pem))
        {
            var rsa = RSA.Create();
            try
            {
                switch (label)
                {
                    case "PRIVATE KEY": rsa.ImportPkcs8PrivateKey(der, out _); return rsa;
                    case "RSA PRIVATE KEY": rsa.ImportRSAPrivateKey(der, out _); return rsa;
                }
            }
            catch (CryptographicException)
            {
                rsa.Dispose();
                throw new OAuthTokenException("The private key in the credential couldn't be read as an RSA key (PEM \"PRIVATE KEY\" or \"RSA PRIVATE KEY\", unencrypted).");
            }
            rsa.Dispose();
        }
        throw new OAuthTokenException("The credential holds no PEM private key (\"-----BEGIN PRIVATE KEY-----\" or \"-----BEGIN RSA PRIVATE KEY-----\").");
    }

    /// <summary>The first PEM CERTIFICATE block and the private key, checked to be a pair.</summary>
    public static CertificateKeyPair ReadCertificateAndKey(string pem)
    {
        var certificateDer = ReadPemBlocks(pem).Where(b => b.Label == "CERTIFICATE").Select(b => b.Der).FirstOrDefault()
            ?? throw new OAuthTokenException("The credential holds no PEM certificate (\"-----BEGIN CERTIFICATE-----\"). Store the certificate and its private key together.");
        var key = ReadRsaPrivateKey(pem);

        byte[]? certificateModulus;
        try
        {
            using var certificate = X509CertificateLoader.LoadCertificate(certificateDer);
            using var publicKey = certificate.GetRSAPublicKey();
            certificateModulus = publicKey?.ExportParameters(false).Modulus;
        }
        catch (CryptographicException)
        {
            key.Dispose();
            throw new OAuthTokenException("The certificate in the credential couldn't be read.");
        }
        if (certificateModulus is null || !certificateModulus.AsSpan().SequenceEqual(key.ExportParameters(false).Modulus))
        {
            key.Dispose();
            throw new OAuthTokenException("The private key in the credential doesn't belong to its certificate (or the certificate's key isn't RSA).");
        }
        return new CertificateKeyPair(certificateDer, key);
    }

    /// <summary>Every PEM block as (label, DER bytes), whitespace (or a literal, JSON-escaped backslash-n) anywhere inside the Base64 ignored.</summary>
    public static IEnumerable<(string Label, byte[] Der)> ReadPemBlocks(string text)
    {
        foreach (Match match in PemBlock().Matches(text))
        {
            byte[] der;
            try
            {
                der = Convert.FromBase64String(Regex.Replace(match.Groups["body"].Value, @"(\s|\\n)+", ""));
            }
            catch (FormatException)
            {
                continue;
            }
            yield return (match.Groups["label"].Value, der);
        }
    }

    [GeneratedRegex(@"-----BEGIN (?<label>[A-Z0-9 ]+)-----(?<body>[A-Za-z0-9+/=\s\\]*?)-----END \k<label>-----")]
    private static partial Regex PemBlock();
}
