using System.Net;
using System.Net.Sockets;
using System.Text;
using BlueTrack.Api.Data;
using BlueTrack.Api.Secrets;
using BlueTrack.Api.Tests.Contract;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-202 against the real provider code, with a stub HTTP handler in place
/// of a CCP server: a timeout falls back to the cache instead of escaping as
/// a 500, http:// is refused, and the registered clients don't follow redirects.
/// </summary>
public class CyberArkCcpFallbackTests : IClassFixture<BlueTrackWebApplicationFactory>
{
    private readonly BlueTrackWebApplicationFactory _factory;

    public CyberArkCcpFallbackTests(BlueTrackWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class StubHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static async Task SetCcpSettingsAsync(string? settings)
    {
        await using var connection = new SqlConnection(TestDatabase.ConnectionString);
        await connection.ExecuteAsync("UPDATE web.secrets_store SET BackendSettings = @settings WHERE BackendType = 'CyberArkCCP'", new { settings });
    }

    private static CyberArkCcpSecretsProvider Provider(HttpMessageHandler handler, SecretFallbackCache cache) =>
        new(new StubHttpClientFactory(handler), new SecretsStoreRepository(new TestDbConnectionFactory(), new WindowsDpapiProtector()), cache);

    [Fact]
    public async Task ATimeout_FallsBackToTheCachedSecret()
    {
        await SetCcpSettingsAsync("""{"BaseUrl":"https://ccp.integration-test.example","AppId":"TestApp"}""");
        try
        {
            var cache = new SecretFallbackCache(TimeProvider.System);
            var query = new SecretQuery("Safe", "Root", "Object");
            var ok = Provider(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"Content":"pw","UserName":"svc","Address":"host"}""", Encoding.UTF8, "application/json")
            }), cache);
            Assert.False((await ok.GetSecretAsync(query)).FromFallbackCache);

            var timingOut = Provider(new StubHandler(_ => throw new TaskCanceledException("timed out")), cache);
            var fallback = await timingOut.GetSecretAsync(query);
            Assert.True(fallback.FromFallbackCache);
            Assert.Equal("pw", new string(fallback.Content));

            // Without a cached value, the timeout is a classified error, not an unhandled exception.
            var ex = await Assert.ThrowsAsync<SecretRetrievalException>(() =>
                Provider(new StubHandler(_ => throw new TaskCanceledException("timed out")), new SecretFallbackCache(TimeProvider.System)).GetSecretAsync(query));
            Assert.Equal(CyberArkErrorCategory.VaultConnectivity, ex.Category);
        }
        finally
        {
            await SetCcpSettingsAsync(null);
        }
    }

    [Fact]
    public async Task AnHttpBaseUrl_IsRefusedBeforeAnyRequest()
    {
        await SetCcpSettingsAsync("""{"BaseUrl":"http://ccp.integration-test.example","AppId":"TestApp"}""");
        try
        {
            var provider = Provider(new StubHandler(_ => throw new InvalidOperationException("no request expected")), new SecretFallbackCache(TimeProvider.System));
            var ex = await Assert.ThrowsAsync<SecretRetrievalException>(() => provider.GetSecretAsync(new SecretQuery("Safe", "Root", "Object")));
            Assert.Contains("https", ex.Message);
        }
        finally
        {
            await SetCcpSettingsAsync(null);
        }
    }

    [Theory]
    [InlineData(nameof(CyberArkCcpSecretsProvider))]
    [InlineData(nameof(CyberArkConjurSecretsProvider))]
    public async Task TheRegisteredClients_DontFollowRedirects(string clientName)
    {
        // A one-shot local server that answers every request with a redirect.
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var serve = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync();
            var stream = socket.GetStream();
            var buffer = new byte[4096];
            _ = await stream.ReadAsync(buffer);
            var reply = Encoding.ASCII.GetBytes($"HTTP/1.1 307 Temporary Redirect\r\nLocation: http://127.0.0.1:{port}/elsewhere\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(reply);
        });

        var client = _factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
        using var response = await client.PostAsync($"http://127.0.0.1:{port}/authenticate", new StringContent("api-key"));

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), client.Timeout);
        await serve;
    }

    [Theory]
    [InlineData("", "Root")]
    [InlineData("  ", "Root")]
    [InlineData("Archive", "Archive")]
    public async Task ABlankFolder_IsSentAsRoot(string folder, string expected)
    {
        await SetCcpSettingsAsync("""{"BaseUrl":"https://ccp.integration-test.example","AppId":"TestApp"}""");
        try
        {
            string? requested = null;
            var provider = Provider(new StubHandler(request =>
            {
                requested = request.RequestUri!.Query;
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"Content":"pw","UserName":"svc","Address":"host"}""", Encoding.UTF8, "application/json")
                };
            }), new SecretFallbackCache(TimeProvider.System));

            await provider.GetSecretAsync(new SecretQuery("Safe", folder, "Object"));

            Assert.Contains($"&Folder={expected}&", requested);
        }
        finally
        {
            await SetCcpSettingsAsync(null);
        }
    }
}
