using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-234: an in-process syslog collector on 127.0.0.1 for the SIEM forwarding tests -- plain TCP, or TLS
/// with a certificate made in the test. Reads each connection to its end and splits it into messages by
/// octet counting (RFC 6587 3.4.1) or LF. Never talks to anything but the loopback address.
/// </summary>
public sealed class SyslogTestListener : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly X509Certificate2? _certificate;
    private readonly string _framing;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private int _connections;

    public ConcurrentQueue<string> Messages { get; } = new();
    public int Port { get; }
    public int Connections => _connections;

    public SyslogTestListener(string framing = "OctetCounting", X509Certificate2? certificate = null, int port = 0)
    {
        _framing = framing;
        _certificate = certificate;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = AcceptLoopAsync();
    }

    /// <summary>A port nothing listens on (a stopped collector).</summary>
    public static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            Interlocked.Increment(ref _connections);
            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                Stream stream = client.GetStream();
                SslStream? ssl = null;
                if (_certificate is not null)
                {
                    ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(_certificate, clientCertificateRequired: false, SslProtocols.None, checkCertificateRevocation: false);
                    stream = ssl;
                }
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, _stop.Token);
                foreach (var message in Split(buffer.ToArray())) Messages.Enqueue(message);
                if (ssl is not null) await ssl.DisposeAsync();
            }
            catch (Exception)
            {
                // A failed handshake (the client refused our certificate) or a reset: nothing received.
            }
        }
    }

    private IEnumerable<string> Split(byte[] data)
    {
        if (_framing == "LineFeed")
        {
            foreach (var line in Encoding.UTF8.GetString(data).Split('\n'))
            {
                if (line.Length > 0) yield return line;
            }
            yield break;
        }

        var position = 0;
        while (position < data.Length)
        {
            var space = Array.IndexOf(data, (byte)' ', position);
            var length = int.Parse(Encoding.ASCII.GetString(data, position, space - position));
            yield return Encoding.UTF8.GetString(data, space + 1, length);
            position = space + 1 + length;
        }
    }

    /// <summary>Waits until at least count messages arrived (or the timeout), then returns them all.</summary>
    public async Task<IReadOnlyList<string>> WaitForAsync(int count, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (Messages.Count < count && DateTime.UtcNow < deadline) await Task.Delay(50);
        return Messages.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
            // Stopping.
        }
        _stop.Dispose();
    }
}

/// <summary>D-234: a test CA and a server certificate it signed for 127.0.0.1/localhost, made in memory.</summary>
public sealed class TestCertificates : IDisposable
{
    public X509Certificate2 Ca { get; }
    public X509Certificate2 Server { get; }
    public string CaPem => Ca.ExportCertificatePem();

    public TestCertificates()
    {
        using var caKey = RSA.Create(2048);
        var caRequest = new CertificateRequest("CN=BlueTrack Test SIEM CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        caRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var caWithKey = caRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        Ca = X509CertificateLoader.LoadCertificate(caWithKey.RawData);

        using var serverKey = RSA.Create(2048);
        var serverRequest = new CertificateRequest("CN=localhost", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        serverRequest.CertificateExtensions.Add(san.Build());
        serverRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        serverRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        serverRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var serial = RandomNumberGenerator.GetBytes(16);
        serial[0] &= 0x7F;
        using var signed = serverRequest.Create(caWithKey, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(29), serial);
        using var withKey = signed.CopyWithPrivateKey(serverKey);
        // SChannel needs the key in a real (PFX-loaded) container, not an ephemeral one.
        Server = X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    public void Dispose()
    {
        Ca.Dispose();
        Server.Dispose();
    }
}
