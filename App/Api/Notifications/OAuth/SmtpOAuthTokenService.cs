using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace BlueTrack.Api.Notifications.OAuth;

/// <summary>
/// D-223: a failure getting an SMTP access token. The message is shown on
/// the Notifications page ("Send Test Email") and logged, so it never
/// includes a secret, an assertion or a token: only the provider's error
/// code and description (with the secret masked, should a provider ever
/// echo it) or what's wrong with the stored key.
/// </summary>
public sealed class OAuthTokenException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>An access token and whether it came from the in-memory cache.</summary>
public sealed record SmtpAccessToken(string Value, bool FromCache);

/// <summary>
/// D-223: gets the SMTP access token for an OAuth2 mode and keeps it in
/// memory until shortly before it expires (a Singleton, so every send in
/// this process shares it). The cache key is a SHA-256 of the settings and
/// the secret, so saving a new secret or setting uses a new token at once;
/// no secret is kept here, only the token.
///
/// Timeouts and retries match D-215's secrets backends: 10 seconds a try,
/// 2 retries, only for a network failure, a timeout, 429 or a 5xx; a 4xx
/// (bad client, bad grant) fails at once. Redirects aren't followed (D-202),
/// so the secret is only ever posted to the address configured.
/// </summary>
public sealed class SmtpOAuthTokenService(IHttpClientFactory httpClientFactory, TimeProvider timeProvider)
{
    public const string HttpClientName = "SmtpOAuth";
    public const int MaxRetries = 2;
    public static readonly TimeSpan AttemptTimeout = TimeSpan.FromSeconds(10);

    /// <summary>A token is used until this long before it expires.</summary>
    public static readonly TimeSpan ExpiryMargin = TimeSpan.FromMinutes(5);

    /// <summary>Pause before a retry, times the attempt number. Tests set it to zero.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset UseUntil)> _cache = new();

    public async Task<SmtpAccessToken> GetTokenAsync(SmtpOAuthSettings settings, string secret, bool forceRefresh = false, CancellationToken cancellationToken = default)
    {
        var cacheKey = CacheKey(settings, secret);
        if (!forceRefresh && _cache.TryGetValue(cacheKey, out var cached) && timeProvider.GetUtcNow() < cached.UseUntil)
        {
            return new SmtpAccessToken(cached.Token, FromCache: true);
        }
        _cache.TryRemove(cacheKey, out _);

        var (token, expiresIn) = await RequestTokenAsync(settings, secret, cancellationToken);
        if (expiresIn is > 0)
        {
            var lifetime = TimeSpan.FromSeconds(expiresIn.Value);
            var useFor = lifetime > ExpiryMargin * 2 ? lifetime - ExpiryMargin : lifetime / 2;
            _cache[cacheKey] = (token, timeProvider.GetUtcNow() + useFor);
        }
        return new SmtpAccessToken(token, FromCache: false);
    }

    private async Task<(string Token, int? ExpiresIn)> RequestTokenAsync(SmtpOAuthSettings settings, string secret, CancellationToken cancellationToken)
    {
        var client = httpClientFactory.CreateClient(HttpClientName);
        var tokenUrl = OAuthTokenRequests.TokenUrlFor(settings);
        for (var attempt = 0; ; attempt++)
        {
            // Built afresh each try: a request can be sent once, and a JWT gets a new jti/iat.
            using var request = OAuthTokenRequests.Build(settings, secret, timeProvider.GetUtcNow());
            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptTimeout.CancelAfter(AttemptTimeout);

            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, attemptTimeout.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
            {
                if (attempt < MaxRetries)
                {
                    await Task.Delay(RetryDelay * (attempt + 1), cancellationToken);
                    continue;
                }
                var reason = ex is OperationCanceledException ? $"no answer within {AttemptTimeout.TotalSeconds:0} seconds" : ex.Message;
                throw new OAuthTokenException($"The token endpoint {tokenUrl} couldn't be reached after {MaxRetries + 1} tries: {Mask(reason, secret)}", ex);
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return ReadTokenResponse(body, tokenUrl);
                }
                if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < MaxRetries)
                {
                    await Task.Delay(RetryDelay * (attempt + 1), cancellationToken);
                    continue;
                }
                throw new OAuthTokenException(DescribeError(response.StatusCode, body, tokenUrl, secret));
            }
        }
    }

    private static (string Token, int? ExpiresIn) ReadTokenResponse(string body, string tokenUrl)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("access_token", out var accessToken) && accessToken.ValueKind == JsonValueKind.String
                && !string.IsNullOrEmpty(accessToken.GetString()))
            {
                int? expiresIn = null;
                if (root.TryGetProperty("expires_in", out var expires))
                {
                    if (expires.ValueKind == JsonValueKind.Number && expires.TryGetInt32(out var seconds)) expiresIn = seconds;
                    else if (expires.ValueKind == JsonValueKind.String && int.TryParse(expires.GetString(), out var parsed)) expiresIn = parsed;
                }
                return (accessToken.GetString()!, expiresIn);
            }
        }
        catch (JsonException)
        {
            // Falls through to the message below.
        }
        throw new OAuthTokenException($"The token endpoint {tokenUrl} answered without an access_token.");
    }

    /// <summary>"Token endpoint returned invalid_client: ..." from an RFC 6749 5.2 error body, or the HTTP status when there isn't one.</summary>
    public static string DescribeError(HttpStatusCode status, string body, string tokenUrl, string secret)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
            {
                var message = $"Token endpoint returned {error.GetString()}";
                if (root.TryGetProperty("error_description", out var description) && description.ValueKind == JsonValueKind.String)
                {
                    // Microsoft's description runs on with trace and correlation IDs: the first line is the reason.
                    var firstLine = (description.GetString() ?? "").Split('\r', '\n')[0].Trim();
                    if (firstLine.Length > 300) firstLine = firstLine[..300] + "...";
                    if (firstLine.Length > 0) message += $": {firstLine}";
                }
                return Mask(message, secret);
            }
        }
        catch (JsonException)
        {
            // Not JSON -- the status alone.
        }
        return $"Token endpoint {tokenUrl} returned HTTP {(int)status} ({status}).";
    }

    private static string Mask(string text, string secret) =>
        secret.Length >= 4 ? text.Replace(secret, "***", StringComparison.Ordinal) : text;

    private static string CacheKey(SmtpOAuthSettings settings, string secret)
    {
        var material = string.Join('\u001f', settings.AuthMethod, settings.Mailbox, settings.TenantId, settings.ClientId,
            settings.ClientCredentialType, settings.TokenUrl, settings.Scope, settings.Audience, settings.ClientAuthStyle, secret);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
