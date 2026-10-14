using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Notifications.OAuth;

/// <summary>
/// D-223: what an OAuth2 SMTP sign-in needs, taken from notification_config
/// (none of it secret). <see cref="Mailbox"/> is the XOAUTH2 user and, for
/// Google, the impersonated user (JWT "sub").
/// </summary>
public sealed record SmtpOAuthSettings(
    string AuthMethod,
    string Mailbox,
    string? TenantId,
    string? ClientId,
    string? ClientCredentialType,
    string? TokenUrl,
    string? Scope,
    string? Audience,
    string? ClientAuthStyle)
{
    public static SmtpOAuthSettings From(NotificationConfig config) => new(
        config.AuthMethod,
        string.IsNullOrWhiteSpace(config.OAuthUsername) ? config.FromAddress ?? "" : config.OAuthUsername.Trim(),
        config.OAuthTenantId?.Trim(),
        config.OAuthClientId?.Trim(),
        config.OAuthClientCredentialType,
        config.OAuthTokenUrl?.Trim(),
        config.OAuthScope?.Trim(),
        config.OAuthAudience?.Trim(),
        config.OAuthClientAuthStyle);
}

/// <summary>
/// D-223: builds the token request for each provider, from primary sources
/// (Design_Notifications.md, "D-223 sources"):
/// <list type="bullet">
/// <item>Microsoft 365: client credentials grant to
/// https://login.microsoftonline.com/{tenant}/oauth2/v2.0/token with scope
/// https://outlook.office365.com/.default, authenticated by client_secret,
/// or by a client_assertion JWT (PS256, "x5t#S256" header; aud = the token
/// endpoint, iss = sub = client ID, jti, nbf, iat, exp).</item>
/// <item>Google Workspace: a service account's RS256 JWT (iss = client_email,
/// scope https://mail.google.com/, aud https://oauth2.googleapis.com/token,
/// sub = the mailbox, exp at most an hour after iat) posted as
/// grant_type=urn:ietf:params:oauth:grant-type:jwt-bearer.</item>
/// <item>Generic: RFC 6749 4.4 client credentials; the client authenticates
/// with HTTP Basic (2.3.1, which every server must support) or the
/// client_id/client_secret form fields; optional scope and audience.</item>
/// </list>
/// Pure: the clock and the secret are passed in, so tests can check every
/// field and signature.
/// </summary>
public static class OAuthTokenRequests
{
    public const string MicrosoftScope = "https://outlook.office365.com/.default";
    public const string GoogleScope = "https://mail.google.com/";
    public const string GoogleTokenUrl = "https://oauth2.googleapis.com/token";
    public const string JwtBearerGrantType = "urn:ietf:params:oauth:grant-type:jwt-bearer";
    public const string JwtBearerClientAssertionType = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer";

    /// <summary>Microsoft's assertion lifetime: "keep it short - 5-10 minutes after nbf at most".</summary>
    public static readonly TimeSpan MicrosoftAssertionLifetime = TimeSpan.FromMinutes(10);

    /// <summary>Google's maximum: "a maximum of 1 hour after the issued time".</summary>
    public static readonly TimeSpan GoogleAssertionLifetime = TimeSpan.FromHours(1);

    public static string MicrosoftTokenUrl(string tenantId) =>
        $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token";

    /// <summary>The token endpoint's address for these settings (for messages; never holds a secret).</summary>
    public static string TokenUrlFor(SmtpOAuthSettings settings) => settings.AuthMethod switch
    {
        SmtpAuthMethods.Microsoft365 => MicrosoftTokenUrl(settings.TenantId ?? ""),
        SmtpAuthMethods.GoogleWorkspace => GoogleTokenUrl,
        _ => settings.TokenUrl ?? ""
    };

    /// <summary>A new request each call (an HttpRequestMessage can be sent once, and each retry needs a fresh assertion).</summary>
    public static HttpRequestMessage Build(SmtpOAuthSettings settings, string secret, DateTimeOffset now) => settings.AuthMethod switch
    {
        SmtpAuthMethods.Microsoft365 => BuildMicrosoft(settings, secret, now),
        SmtpAuthMethods.GoogleWorkspace => BuildGoogle(settings, secret, now),
        SmtpAuthMethods.GenericOAuth2 => BuildGeneric(settings, secret),
        _ => throw new InvalidOperationException($"'{settings.AuthMethod}' isn't an OAuth2 sign-in mode.")
    };

    private static HttpRequestMessage BuildMicrosoft(SmtpOAuthSettings settings, string secret, DateTimeOffset now)
    {
        var clientId = settings.ClientId ?? "";
        var tokenUrl = MicrosoftTokenUrl(settings.TenantId ?? "");
        List<KeyValuePair<string, string>> form =
        [
            new("client_id", clientId),
            new("scope", MicrosoftScope),
            new("grant_type", "client_credentials")
        ];

        if (settings.ClientCredentialType == "Certificate")
        {
            var pair = OAuthKeyMaterial.ReadCertificateAndKey(secret);
            using var key = pair.PrivateKey;
            var header = new Dictionary<string, object>
            {
                ["alg"] = "PS256",
                ["typ"] = "JWT",
                ["x5t#S256"] = Base64Url(SHA256.HashData(pair.CertificateDer))
            };
            var issuedAt = now.ToUnixTimeSeconds();
            var claims = new Dictionary<string, object>
            {
                ["aud"] = tokenUrl,
                ["exp"] = now.Add(MicrosoftAssertionLifetime).ToUnixTimeSeconds(),
                ["iss"] = clientId,
                ["jti"] = Guid.NewGuid().ToString(),
                ["nbf"] = issuedAt,
                ["sub"] = clientId,
                ["iat"] = issuedAt
            };
            form.Add(new("client_assertion_type", JwtBearerClientAssertionType));
            form.Add(new("client_assertion", SignJwt(header, claims, key, RSASignaturePadding.Pss)));
        }
        else
        {
            form.Add(new("client_secret", secret));
        }
        return new HttpRequestMessage(HttpMethod.Post, tokenUrl) { Content = new FormUrlEncodedContent(form) };
    }

    private static HttpRequestMessage BuildGoogle(SmtpOAuthSettings settings, string secret, DateTimeOffset now)
    {
        var serviceAccount = OAuthKeyMaterial.ReadGoogleServiceAccountKey(secret);
        using var key = OAuthKeyMaterial.ReadRsaPrivateKey(serviceAccount.PrivateKeyPem);
        var header = new Dictionary<string, object> { ["alg"] = "RS256", ["typ"] = "JWT" };
        if (!string.IsNullOrEmpty(serviceAccount.PrivateKeyId)) header["kid"] = serviceAccount.PrivateKeyId;
        var claims = new Dictionary<string, object>
        {
            ["iss"] = serviceAccount.ClientEmail,
            ["scope"] = GoogleScope,
            ["aud"] = GoogleTokenUrl,
            ["exp"] = now.Add(GoogleAssertionLifetime).ToUnixTimeSeconds(),
            ["iat"] = now.ToUnixTimeSeconds(),
            ["sub"] = settings.Mailbox
        };
        var assertion = SignJwt(header, claims, key, RSASignaturePadding.Pkcs1);
        return new HttpRequestMessage(HttpMethod.Post, GoogleTokenUrl)
        {
            Content = new FormUrlEncodedContent([new("grant_type", JwtBearerGrantType), new("assertion", assertion)])
        };
    }

    private static HttpRequestMessage BuildGeneric(SmtpOAuthSettings settings, string secret)
    {
        var clientId = settings.ClientId ?? "";
        List<KeyValuePair<string, string>> form = [new("grant_type", "client_credentials")];
        if (!string.IsNullOrWhiteSpace(settings.Scope)) form.Add(new("scope", settings.Scope));
        if (!string.IsNullOrWhiteSpace(settings.Audience)) form.Add(new("audience", settings.Audience));

        var request = new HttpRequestMessage(HttpMethod.Post, settings.TokenUrl);
        if (settings.ClientAuthStyle == "Body")
        {
            form.Add(new("client_id", clientId));
            form.Add(new("client_secret", secret));
        }
        else
        {
            // RFC 6749 2.3.1: each is form-urlencoded, then joined with ':' and Base64-encoded.
            var basic = $"{FormEncode(clientId)}:{FormEncode(secret)}";
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(basic)));
        }
        request.Content = new FormUrlEncodedContent(form);
        return request;
    }

    /// <summary>application/x-www-form-urlencoded, as FormUrlEncodedContent encodes a value (space as '+').</summary>
    private static string FormEncode(string value) => Uri.EscapeDataString(value).Replace("%20", "+");

    public static string SignJwt(IReadOnlyDictionary<string, object> header, IReadOnlyDictionary<string, object> claims, RSA key, RSASignaturePadding padding)
    {
        var signingInput = $"{Base64Url(JsonSerializer.SerializeToUtf8Bytes(header))}.{Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims))}";
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, padding);
        return $"{signingInput}.{Base64Url(signature)}";
    }

    public static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
