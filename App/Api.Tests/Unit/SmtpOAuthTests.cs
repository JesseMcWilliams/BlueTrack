using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using BlueTrack.Api.Models;
using BlueTrack.Api.Notifications;
using BlueTrack.Api.Notifications.OAuth;
using MailKit.Security;
using MimeKit;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>
/// D-223: OAuth2 (XOAUTH2) SMTP sign-in -- token requests per provider
/// (form fields, scopes, JWT claims and signatures checked with keys made
/// here), token caching, refresh-once, retries, secret-free errors, and
/// which MailKit sign-in each mode uses. No real token endpoint or SMTP
/// server is called: a fake HttpMessageHandler and a fake transport.
/// </summary>
public class SmtpOAuthTests
{
    private const string Secret = "ThisIsMy_FAKE_Password6!";
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    // ---- token requests ------------------------------------------------

    [Fact]
    public async Task Microsoft365_Secret_PostsClientCredentialsToTheTenantEndpoint_WithTheOutlookScope()
    {
        var settings = M365Settings("Secret");
        using var request = OAuthTokenRequests.Build(settings, Secret, Now);

        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://login.microsoftonline.com/contoso.onmicrosoft.com/oauth2/v2.0/token", request.RequestUri!.ToString());
        var form = await ReadForm(request);
        Assert.Equal("11112222-bbbb-3333-cccc-4444dddd5555", form["client_id"]);
        Assert.Equal("https://outlook.office365.com/.default", form["scope"]);
        Assert.Equal("client_credentials", form["grant_type"]);
        Assert.Equal(Secret, form["client_secret"]);
        Assert.False(form.ContainsKey("client_assertion"));
    }

    [Fact]
    public async Task Microsoft365_Certificate_SendsAPs256ClientAssertion_ThatVerifiesWithTheCertificate()
    {
        using var rsa = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=BlueTrack SMTP test", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(Now.AddDays(-1), Now.AddDays(30));
        // A single line, as the Credentials page's password box leaves it.
        var pem = (certificate.ExportCertificatePem() + rsa.ExportPkcs8PrivateKeyPem()).Replace("\r", "").Replace("\n", "");

        var settings = M365Settings("Certificate");
        using var request = OAuthTokenRequests.Build(settings, pem, Now);
        var form = await ReadForm(request);

        Assert.False(form.ContainsKey("client_secret"));
        Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", form["client_assertion_type"]);
        Assert.Equal("https://outlook.office365.com/.default", form["scope"]);
        var (header, claims, signingInput, signature) = SplitJwt(form["client_assertion"]);

        Assert.Equal("PS256", header.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.GetProperty("typ").GetString());
        Assert.Equal(OAuthTokenRequests.Base64Url(SHA256.HashData(certificate.RawData)), header.GetProperty("x5t#S256").GetString());
        Assert.Equal("https://login.microsoftonline.com/contoso.onmicrosoft.com/oauth2/v2.0/token", claims.GetProperty("aud").GetString());
        Assert.Equal("11112222-bbbb-3333-cccc-4444dddd5555", claims.GetProperty("iss").GetString());
        Assert.Equal("11112222-bbbb-3333-cccc-4444dddd5555", claims.GetProperty("sub").GetString());
        Assert.True(Guid.TryParse(claims.GetProperty("jti").GetString(), out _));
        Assert.Equal(Now.ToUnixTimeSeconds(), claims.GetProperty("nbf").GetInt64());
        Assert.Equal(Now.ToUnixTimeSeconds(), claims.GetProperty("iat").GetInt64());
        Assert.Equal(Now.AddMinutes(10).ToUnixTimeSeconds(), claims.GetProperty("exp").GetInt64());

        using var publicKey = certificate.GetRSAPublicKey()!;
        Assert.True(publicKey.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    }

    [Fact]
    public void Microsoft365_Certificate_WithAKeyFromAnotherCertificate_IsRefused_WithoutTheKeyInTheMessage()
    {
        using var certificateKey = RSA.Create(2048);
        using var otherKey = RSA.Create(2048);
        using var certificate = new CertificateRequest("CN=x", certificateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
            .CreateSelfSigned(Now.AddDays(-1), Now.AddDays(1));
        var otherKeyPem = otherKey.ExportPkcs8PrivateKeyPem();

        var ex = Assert.Throws<OAuthTokenException>(() => OAuthTokenRequests.Build(M365Settings("Certificate"), certificate.ExportCertificatePem() + otherKeyPem, Now));
        Assert.Contains("doesn't belong to its certificate", ex.Message);
        Assert.DoesNotContain("PRIVATE KEY-----", ex.Message);
    }

    [Fact]
    public async Task Google_SendsAnRs256JwtBearerAssertion_ImpersonatingTheMailbox_ThatVerifiesWithTheServiceAccountKey()
    {
        using var rsa = RSA.Create(2048);
        var keyJson = GoogleKeyJson(rsa);
        var settings = new SmtpOAuthSettings(SmtpAuthMethods.GoogleWorkspace, "alerts@example.com", null, null, null, null, null, null, null);

        using var request = OAuthTokenRequests.Build(settings, keyJson, Now);

        Assert.Equal("https://oauth2.googleapis.com/token", request.RequestUri!.ToString());
        var form = await ReadForm(request);
        Assert.Equal("urn:ietf:params:oauth:grant-type:jwt-bearer", form["grant_type"]);
        var (header, claims, signingInput, signature) = SplitJwt(form["assertion"]);

        Assert.Equal("RS256", header.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.GetProperty("typ").GetString());
        Assert.Equal("key-id-123", header.GetProperty("kid").GetString());
        Assert.Equal("bluetrack@project.iam.gserviceaccount.com", claims.GetProperty("iss").GetString());
        Assert.Equal("https://mail.google.com/", claims.GetProperty("scope").GetString());
        Assert.Equal("https://oauth2.googleapis.com/token", claims.GetProperty("aud").GetString());
        Assert.Equal("alerts@example.com", claims.GetProperty("sub").GetString());
        Assert.Equal(3600, claims.GetProperty("exp").GetInt64() - claims.GetProperty("iat").GetInt64());
        Assert.True(rsa.VerifyData(signingInput, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    [Fact]
    public void Google_ACredentialThatIsNotAServiceAccountKey_IsRefused_WithoutEchoingIt()
    {
        var settings = new SmtpOAuthSettings(SmtpAuthMethods.GoogleWorkspace, "alerts@example.com", null, null, null, null, null, null, null);
        var ex = Assert.Throws<OAuthTokenException>(() => OAuthTokenRequests.Build(settings, Secret, Now));
        Assert.Contains("service account JSON key", ex.Message);
        Assert.DoesNotContain(Secret, ex.Message);
    }

    [Fact]
    public async Task Generic_Basic_PutsTheClientInTheAuthorizationHeader_NotTheBody()
    {
        var settings = GenericSettings("Basic");
        using var request = OAuthTokenRequests.Build(settings, "s3cret+/ value", Now);

        Assert.Equal("https://idp.example.com/oauth2/token", request.RequestUri!.ToString());
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(request.Headers.Authorization.Parameter!));
        Assert.Equal("my+client:s3cret%2B%2F+value", decoded);   // RFC 6749 2.3.1: each form-urlencoded first

        var form = await ReadForm(request);
        Assert.Equal("client_credentials", form["grant_type"]);
        Assert.Equal("smtp.send mail.write", form["scope"]);
        Assert.Equal("https://smtp.example.com", form["audience"]);
        Assert.False(form.ContainsKey("client_secret"));
        Assert.False(form.ContainsKey("client_id"));
    }

    [Fact]
    public async Task Generic_Body_SendsClientIdAndSecretAsFormFields_AndLeavesOutBlankScopeAndAudience()
    {
        var settings = GenericSettings("Body") with { Scope = null, Audience = "" };
        using var request = OAuthTokenRequests.Build(settings, Secret, Now);

        Assert.Null(request.Headers.Authorization);
        var form = await ReadForm(request);
        Assert.Equal("my client", form["client_id"]);
        Assert.Equal(Secret, form["client_secret"]);
        Assert.False(form.ContainsKey("scope"));
        Assert.False(form.ContainsKey("audience"));
    }

    // ---- token service: caching, retries, errors -------------------------

    [Fact]
    public async Task TokenService_CachesUntilFiveMinutesBeforeExpiry_ThenFetchesAgain()
    {
        var handler = new FakeTokenEndpoint();
        handler.Enqueue(HttpStatusCode.OK, """{"access_token":"token-1","token_type":"Bearer","expires_in":3600}""");
        handler.Enqueue(HttpStatusCode.OK, """{"access_token":"token-2","token_type":"Bearer","expires_in":3600}""");
        var clock = new ManualClock(Now);
        var service = CreateService(handler, clock);
        var settings = GenericSettings("Body");

        var first = await service.GetTokenAsync(settings, Secret);
        Assert.Equal(("token-1", false), (first.Value, first.FromCache));

        clock.Advance(TimeSpan.FromMinutes(54));
        var second = await service.GetTokenAsync(settings, Secret);
        Assert.Equal(("token-1", true), (second.Value, second.FromCache));
        Assert.Equal(1, handler.Calls);

        clock.Advance(TimeSpan.FromMinutes(1));   // 55 minutes: within the 5-minute margin
        var third = await service.GetTokenAsync(settings, Secret);
        Assert.Equal(("token-2", false), (third.Value, third.FromCache));
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task TokenService_ANewSecretOrSetting_DoesNotReuseTheCachedToken_AndForceRefreshSkipsTheCache()
    {
        var handler = new FakeTokenEndpoint();
        for (var i = 1; i <= 4; i++) handler.Enqueue(HttpStatusCode.OK, $$"""{"access_token":"token-{{i}}","expires_in":3600}""");
        var service = CreateService(handler, new ManualClock(Now));
        var settings = GenericSettings("Body");

        Assert.Equal("token-1", (await service.GetTokenAsync(settings, Secret)).Value);
        Assert.Equal("token-2", (await service.GetTokenAsync(settings, Secret + "x")).Value);
        Assert.Equal("token-3", (await service.GetTokenAsync(settings with { Scope = "other" }, Secret)).Value);
        Assert.Equal("token-4", (await service.GetTokenAsync(settings, Secret, forceRefresh: true)).Value);
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task TokenService_RetriesA5xxTwice_ThenSucceeds()
    {
        var handler = new FakeTokenEndpoint();
        handler.Enqueue(HttpStatusCode.ServiceUnavailable, "");
        handler.Enqueue(HttpStatusCode.TooManyRequests, "");
        handler.Enqueue(HttpStatusCode.OK, """{"access_token":"token-1","expires_in":3600}""");
        var service = CreateService(handler, new ManualClock(Now));

        Assert.Equal("token-1", (await service.GetTokenAsync(GenericSettings("Body"), Secret)).Value);
        Assert.Equal(3, handler.Calls);
    }

    [Fact]
    public async Task TokenService_GivesUpAfterTwoRetries_OnNetworkFailures()
    {
        var handler = new FakeTokenEndpoint();
        for (var i = 0; i < 3; i++) handler.EnqueueFailure(new HttpRequestException("No such host is known."));
        var service = CreateService(handler, new ManualClock(Now));

        var ex = await Assert.ThrowsAsync<OAuthTokenException>(() => service.GetTokenAsync(GenericSettings("Body"), Secret));
        Assert.Equal(3, handler.Calls);
        Assert.Contains("couldn't be reached after 3 tries", ex.Message);
        Assert.Contains("https://idp.example.com/oauth2/token", ex.Message);
    }

    [Fact]
    public async Task TokenService_InvalidClient_FailsAtOnce_WithTheErrorCode_AndTheSecretMasked()
    {
        var handler = new FakeTokenEndpoint();
        // A provider echoing the secret back is unlikely, but must not leak it.
        handler.Enqueue(HttpStatusCode.Unauthorized,
            $$"""{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided ({{Secret}}).\r\nTrace ID: abc\r\nCorrelation ID: def"}""");
        var service = CreateService(handler, new ManualClock(Now));

        var ex = await Assert.ThrowsAsync<OAuthTokenException>(() => service.GetTokenAsync(GenericSettings("Body"), Secret));
        Assert.Equal(1, handler.Calls);
        Assert.StartsWith("Token endpoint returned invalid_client: AADSTS7000215: Invalid client secret provided", ex.Message);
        Assert.DoesNotContain(Secret, ex.Message);
        Assert.DoesNotContain("Trace ID", ex.Message);
    }

    [Fact]
    public async Task TokenService_ANonJsonError_ReportsTheStatus_AndASuccessWithoutAToken_IsAnError()
    {
        var handler = new FakeTokenEndpoint();
        handler.Enqueue(HttpStatusCode.NotFound, "<html>not here</html>");
        handler.Enqueue(HttpStatusCode.OK, """{"token_type":"Bearer"}""");
        var service = CreateService(handler, new ManualClock(Now));

        var notFound = await Assert.ThrowsAsync<OAuthTokenException>(() => service.GetTokenAsync(GenericSettings("Body"), Secret));
        Assert.Equal("Token endpoint https://idp.example.com/oauth2/token returned HTTP 404 (NotFound).", notFound.Message);
        var noToken = await Assert.ThrowsAsync<OAuthTokenException>(() => service.GetTokenAsync(GenericSettings("Body"), Secret));
        Assert.Contains("without an access_token", noToken.Message);
    }

    // ---- the sender: which sign-in per mode -------------------------------

    [Fact]
    public async Task Sender_Basic_SignsInWithUsernameAndPassword()
    {
        var (sender, transports, _) = CreateSender(new FakeTokenEndpoint());
        await sender.SendAsync(Config(SmtpAuthMethods.Basic), _ => Task.FromResult<(string?, string)>(("smtp-user", Secret)), ["to@example.com"], "s", "b");

        var transport = Assert.Single(transports);
        Assert.Equal(["connect", "auth:password smtp-user", "send", "disconnect"], transport.Log);
    }

    [Fact]
    public async Task Sender_None_DoesNotSignIn()
    {
        var (sender, transports, handler) = CreateSender(new FakeTokenEndpoint());
        await sender.SendAsync(Config(SmtpAuthMethods.None), _ => throw new InvalidOperationException("not used"), ["to@example.com"], "s", "b");

        Assert.Equal(["connect", "send", "disconnect"], Assert.Single(transports).Log);
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(SmtpAuthMethods.Microsoft365)]
    [InlineData(SmtpAuthMethods.GenericOAuth2)]
    public async Task Sender_OAuthModes_SignInWithXOAuth2_AsTheMailbox(string authMethod)
    {
        var handler = new FakeTokenEndpoint();
        handler.Enqueue(HttpStatusCode.OK, """{"access_token":"the-token","expires_in":3600}""");
        var (sender, transports, _) = CreateSender(handler);

        await sender.SendAsync(Config(authMethod), _ => Task.FromResult<(string?, string)>((null, Secret)), ["to@example.com"], "s", "b");

        var transport = Assert.Single(transports);
        Assert.Equal(["connect", "auth:XOAUTH2 alerts@example.com the-token", "send", "disconnect"], transport.Log);
    }

    [Fact]
    public async Task Sender_OAuth_UsesTheFromAddressWhenNoMailboxIsSet()
    {
        var handler = new FakeTokenEndpoint();
        handler.Enqueue(HttpStatusCode.OK, """{"access_token":"the-token","expires_in":3600}""");
        var (sender, transports, _) = CreateSender(handler);
        var config = Config(SmtpAuthMethods.GenericOAuth2, oauthUsername: null);

        await sender.SendAsync(config, _ => Task.FromResult<(string?, string)>((null, Secret)), ["to@example.com"], "s", "b");

        Assert.Contains("auth:XOAUTH2 bluetrack@example.com the-token", Assert.Single(transports).Log);
    }

    [Fact]
    public async Task Sender_OAuth_ARejectedToken_IsRefreshedOnce_ThenTheSendGoesAhead()
    {
        var handler = new FakeTokenEndpoint();
        handler.Enqueue(HttpStatusCode.OK, """{"access_token":"stale-token","expires_in":3600}""");
        handler.Enqueue(HttpStatusCode.OK, """{"access_token":"fresh-token","expires_in":3600}""");
        var (sender, transports, _) = CreateSender(handler, rejectFirstOAuth: 1);

        await sender.SendAsync(Config(SmtpAuthMethods.GenericOAuth2), _ => Task.FromResult<(string?, string)>((null, Secret)), ["to@example.com"], "s", "b");

        Assert.Equal(2, handler.Calls);
        Assert.Equal(
            ["connect", "auth:XOAUTH2 alerts@example.com stale-token", "rejected", "auth:XOAUTH2 alerts@example.com fresh-token", "send", "disconnect"],
            Assert.Single(transports).Log);
    }

    [Fact]
    public async Task Sender_OAuth_RejectedTwice_FailsWithAHint_AndNeverSends()
    {
        var handler = new FakeTokenEndpoint();
        handler.Enqueue(HttpStatusCode.OK, """{"access_token":"token-1","expires_in":3600}""");
        handler.Enqueue(HttpStatusCode.OK, """{"access_token":"token-2","expires_in":3600}""");
        var (sender, transports, _) = CreateSender(handler, rejectFirstOAuth: 2);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sender.SendAsync(Config(SmtpAuthMethods.Microsoft365), _ => Task.FromResult<(string?, string)>((null, Secret)), ["to@example.com"], "s", "b"));

        Assert.Contains("rejected the OAuth2 sign-in for 'alerts@example.com'", ex.Message);
        Assert.Contains("New-ServicePrincipal", ex.Message);
        Assert.DoesNotContain("token-", ex.Message);
        Assert.DoesNotContain("send", Assert.Single(transports).Log);
    }

    [Fact]
    public async Task Sender_OAuth_NeverSendsTheTokenOverAnUnencryptedConnection()
    {
        var handler = new FakeTokenEndpoint();
        handler.Enqueue(HttpStatusCode.OK, """{"access_token":"the-token","expires_in":3600}""");
        var (sender, transports, _) = CreateSender(handler, secure: false);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            sender.SendAsync(Config(SmtpAuthMethods.GenericOAuth2), _ => Task.FromResult<(string?, string)>((null, Secret)), ["to@example.com"], "s", "b"));

        Assert.Contains("needs STARTTLS", ex.Message);
        Assert.Equal(["connect"], Assert.Single(transports).Log);
    }

    [Fact]
    public async Task Sender_OAuth_ATokenFailure_IsReportedBeforeConnectingToTheSmtpServer()
    {
        var handler = new FakeTokenEndpoint();
        handler.Enqueue(HttpStatusCode.BadRequest, """{"error":"invalid_scope","error_description":"bad scope"}""");
        var (sender, transports, _) = CreateSender(handler);

        var ex = await Assert.ThrowsAsync<OAuthTokenException>(() =>
            sender.SendAsync(Config(SmtpAuthMethods.GenericOAuth2), _ => Task.FromResult<(string?, string)>((null, Secret)), ["to@example.com"], "s", "b"));

        Assert.Equal("Token endpoint returned invalid_scope: bad scope", ex.Message);
        Assert.Empty(transports);
    }

    // ---- save validation ---------------------------------------------------

    [Fact]
    public void Validate_ListsEachMissingField_PerMode()
    {
        Assert.Empty(SmtpAuthMethods.Validate(Request(SmtpAuthMethods.Basic)));
        Assert.Contains("AuthMethod must be one of", Assert.Single(SmtpAuthMethods.Validate(Request("OAuth2"))));

        var m365 = string.Join(" ", SmtpAuthMethods.Validate(Request(SmtpAuthMethods.Microsoft365, startTls: false, credentialKey: null)));
        Assert.Contains("STARTTLS must be on", m365);
        Assert.Contains("client secret", m365);
        Assert.Contains("Tenant ID is required", m365);
        Assert.Contains("Client ID is required", m365);
        Assert.Contains("Secret or Certificate", m365);

        var badTenant = Request(SmtpAuthMethods.Microsoft365, tenantId: "contoso/../evil", clientId: "id", credentialType: "Secret");
        Assert.Contains("GUID or domain name", Assert.Single(SmtpAuthMethods.Validate(badTenant)));

        var google = SmtpAuthMethods.Validate(Request(SmtpAuthMethods.GoogleWorkspace, credentialKey: null, fromAddress: null));
        Assert.Contains(google, e => e.Contains("JSON key"));
        Assert.Contains(google, e => e.Contains("mailbox to sign in as"));

        var http = Request(SmtpAuthMethods.GenericOAuth2, tokenUrl: "http://idp.example.com/token", clientId: "id", authStyle: "Basic");
        Assert.Contains("https://", Assert.Single(SmtpAuthMethods.Validate(http)));
        var generic = string.Join(" ", SmtpAuthMethods.Validate(Request(SmtpAuthMethods.GenericOAuth2)));
        Assert.Contains("Token URL is required", generic);
        Assert.Contains("Basic (Authorization header) or Body", generic);

        var valid = Request(SmtpAuthMethods.GenericOAuth2, tokenUrl: "https://idp.example.com/token", clientId: "id", authStyle: "Body");
        Assert.Empty(SmtpAuthMethods.Validate(valid));
    }

    // ---- helpers ---------------------------------------------------------------

    private static SmtpOAuthSettings M365Settings(string credentialType) => new(
        SmtpAuthMethods.Microsoft365, "alerts@example.com", "contoso.onmicrosoft.com", "11112222-bbbb-3333-cccc-4444dddd5555",
        credentialType, null, null, null, null);

    private static SmtpOAuthSettings GenericSettings(string authStyle) => new(
        SmtpAuthMethods.GenericOAuth2, "alerts@example.com", null, "my client", null,
        "https://idp.example.com/oauth2/token", "smtp.send mail.write", "https://smtp.example.com", authStyle);

    private static NotificationConfig Config(string authMethod, string? oauthUsername = "alerts@example.com") => new()
    {
        SmtpHost = "smtp.example.com",
        SmtpPort = 587,
        EnableStartTls = true,
        AuthMethod = authMethod,
        SmtpCredentialKey = 7,
        FromAddress = "bluetrack@example.com",
        OAuthTenantId = "contoso.onmicrosoft.com",
        OAuthClientId = "client",
        OAuthClientCredentialType = "Secret",
        OAuthTokenUrl = "https://idp.example.com/oauth2/token",
        OAuthClientAuthStyle = "Body",
        OAuthUsername = oauthUsername
    };

    private static SaveNotificationConfigRequest Request(
        string authMethod, bool startTls = true, int? credentialKey = 7, string? fromAddress = "bluetrack@example.com",
        string? tenantId = null, string? clientId = null, string? credentialType = null, string? tokenUrl = null, string? authStyle = null) => new()
    {
        SmtpHost = "smtp.example.com",
        SmtpPort = 587,
        EnableStartTls = startTls,
        AuthMethod = authMethod,
        SmtpCredentialKey = credentialKey,
        FromAddress = fromAddress,
        OAuthTenantId = tenantId,
        OAuthClientId = clientId,
        OAuthClientCredentialType = credentialType,
        OAuthTokenUrl = tokenUrl,
        OAuthClientAuthStyle = authStyle
    };

    private static string GoogleKeyJson(RSA rsa) => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["type"] = "service_account",
        ["project_id"] = "project",
        ["private_key_id"] = "key-id-123",
        ["private_key"] = rsa.ExportPkcs8PrivateKeyPem() + "\n",
        ["client_email"] = "bluetrack@project.iam.gserviceaccount.com",
        ["token_uri"] = "https://oauth2.googleapis.com/token"
    });

    private static async Task<Dictionary<string, string>> ReadForm(HttpRequestMessage request)
    {
        var body = await request.Content!.ReadAsStringAsync();
        return body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => Decode(p[0]), p => Decode(p[1]));
        static string Decode(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
    }

    private static (JsonElement Header, JsonElement Claims, byte[] SigningInput, byte[] Signature) SplitJwt(string jwt)
    {
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        static byte[] FromBase64Url(string s)
        {
            s = s.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
        }
        return (JsonDocument.Parse(FromBase64Url(parts[0])).RootElement,
                JsonDocument.Parse(FromBase64Url(parts[1])).RootElement,
                Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"),
                FromBase64Url(parts[2]));
    }

    private static SmtpOAuthTokenService CreateService(FakeTokenEndpoint handler, TimeProvider clock) =>
        new(new FakeHttpClientFactory(handler), clock) { RetryDelay = TimeSpan.Zero };

    private static (SmtpNotificationSender Sender, List<FakeTransport> Transports, FakeTokenEndpoint Handler) CreateSender(
        FakeTokenEndpoint handler, int rejectFirstOAuth = 0, bool secure = true)
    {
        var factory = new FakeTransportFactory(rejectFirstOAuth, secure);
        // The repositories aren't used by the SendAsync(config, ...) overload.
        var sender = new SmtpNotificationSender(null!, null!, factory, CreateService(handler, new ManualClock(Now)));
        return (sender, factory.Created, handler);
    }

    private sealed class ManualClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    /// <summary>Answers each request with the next queued response (or throws the next queued failure).</summary>
    private sealed class FakeTokenEndpoint : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses = new();
        public int Calls { get; private set; }

        public void Enqueue(HttpStatusCode status, string body) =>
            _responses.Enqueue(() => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

        public void EnqueueFailure(Exception ex) => _responses.Enqueue(() => throw ex);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Assert.True(_responses.Count > 0, "Unexpected token request");
            return Task.FromResult(_responses.Dequeue()());
        }
    }

    private sealed class FakeTransportFactory(int rejectFirstOAuth, bool secure) : ISmtpTransportFactory
    {
        public List<FakeTransport> Created { get; } = [];
        private int _rejectionsLeft = rejectFirstOAuth;

        public ISmtpTransport Create(bool ignoreCrlErrors, bool ignoreSslErrors)
        {
            var transport = new FakeTransport(secure, () => _rejectionsLeft-- > 0);
            Created.Add(transport);
            return transport;
        }
    }

    private sealed class FakeTransport(bool secure, Func<bool> reject) : ISmtpTransport
    {
        public List<string> Log { get; } = [];
        public bool IsConnected { get; private set; }
        public bool IsSecure => secure && IsConnected;

        public Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default)
        {
            Log.Add("connect");
            IsConnected = true;
            return Task.CompletedTask;
        }

        public Task AuthenticateAsync(string userName, string password, CancellationToken cancellationToken = default)
        {
            Log.Add($"auth:password {userName}");
            return Task.CompletedTask;
        }

        public Task AuthenticateAsync(SaslMechanism mechanism, CancellationToken cancellationToken = default)
        {
            Assert.IsType<SaslMechanismOAuth2>(mechanism);
            Assert.Equal("XOAUTH2", mechanism.MechanismName);
            Log.Add($"auth:XOAUTH2 {mechanism.Credentials.UserName} {mechanism.Credentials.Password}");
            if (reject())
            {
                Log.Add("rejected");
                throw new AuthenticationException("535: 5.7.3 Authentication unsuccessful");
            }
            return Task.CompletedTask;
        }

        public Task SendAsync(MimeMessage message, CancellationToken cancellationToken = default)
        {
            Log.Add("send");
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default)
        {
            Log.Add("disconnect");
            IsConnected = false;
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
