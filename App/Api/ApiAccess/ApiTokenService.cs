using System.Security.Cryptography;
using BlueTrack.Api.Data;
using BlueTrack.Api.Secrets;
using Dapper;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace BlueTrack.Api.ApiAccess;

/// <summary>
/// Who a token was issued to: an API key (ApiKeyKey set), or a caller with an identity provider's token
/// (MappingKey and RoleKey set). Subject is the identifier that caller's app_user has.
/// </summary>
public sealed record ApiTokenIdentity(int? ApiKeyKey, string DisplayName, string Subject, int? MappingKey = null, int? RoleKey = null);

/// <summary>
/// D-243: issues and checks the short-lived tokens an API key is exchanged
/// for. A token is a JWT signed with HS256 by a secret BlueTrack makes itself
/// (web.api_signing_key, protected with Windows DPAPI), so it is only ever
/// valid for this BlueTrack. It carries no permissions: what the caller may
/// do is looked up from the key's role on every request, so a role or key
/// change takes effect straight away.
/// </summary>
public sealed class ApiTokenService(IDbConnectionFactory connectionFactory, ILocalSecretProtector protector, TimeProvider timeProvider)
{
    public const string Issuer = "BlueTrack";
    public const string Audience = "BlueTrack.Api";
    public const string KeyKeyClaim = "bt_key";
    public const string MappingKeyClaim = "bt_map";
    public const string RoleKeyClaim = "bt_role";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private (string KeyId, byte[] Key)? _signingKey;

    public Task<(string Token, int ExpiresInSeconds)> IssueAsync(int apiKeyKey, string displayName) =>
        CreateAsync(ApiAccessRules.ExternalIdentifierFor(apiKeyKey), displayName, new Dictionary<string, object> { [KeyKeyClaim] = apiKeyKey });

    /// <summary>A token for a caller with an identity provider's token: the mapping and role it matched are fixed in it.</summary>
    public Task<(string Token, int ExpiresInSeconds)> IssueForClientAsync(string subject, string displayName, int mappingKey, int roleKey) =>
        CreateAsync(subject, displayName, new Dictionary<string, object> { [MappingKeyClaim] = mappingKey, [RoleKeyClaim] = roleKey });

    private async Task<(string Token, int ExpiresInSeconds)> CreateAsync(string subject, string displayName, Dictionary<string, object> extraClaims)
    {
        var (keyId, key) = await GetSigningKeyAsync();
        var now = timeProvider.GetUtcNow();
        var claims = new Dictionary<string, object>
        {
            [JwtRegisteredClaimNames.Sub] = subject,
            [JwtRegisteredClaimNames.Jti] = Guid.NewGuid().ToString("N"),
            ["name"] = displayName,
        };
        foreach (var (type, value) in extraClaims) claims[type] = value;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = now.Add(Lifetime).UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key) { KeyId = keyId }, SecurityAlgorithms.HmacSha256),
        };
        return (new JsonWebTokenHandler().CreateToken(descriptor), (int)Lifetime.TotalSeconds);
    }

    /// <summary>The key the token was issued for, or null when the token is malformed, forged or expired.</summary>
    public async Task<ApiTokenIdentity?> ValidateAsync(string token)
    {
        var (_, key) = await GetSigningKeyAsync();
        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidateIssuerSigningKey = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = ClockSkew,
            LifetimeValidator = (notBefore, expires, _, _) =>
                expires is not null && expires.Value.ToUniversalTime() > timeProvider.GetUtcNow().UtcDateTime - ClockSkew
                && (notBefore is null || notBefore.Value.ToUniversalTime() <= timeProvider.GetUtcNow().UtcDateTime + ClockSkew),
        });
        if (!result.IsValid) return null;

        var claims = result.ClaimsIdentity;
        var name = claims.FindFirst("name")?.Value;
        var subject = claims.FindFirst(JwtRegisteredClaimNames.Sub)?.Value;
        if (name is null || subject is null) return null;

        if (int.TryParse(claims.FindFirst(KeyKeyClaim)?.Value, out var apiKeyKey))
        {
            return new ApiTokenIdentity(apiKeyKey, name, subject);
        }
        return int.TryParse(claims.FindFirst(MappingKeyClaim)?.Value, out var mappingKey) && int.TryParse(claims.FindFirst(RoleKeyClaim)?.Value, out var roleKey)
            ? new ApiTokenIdentity(null, name, subject, mappingKey, roleKey)
            : null;
    }

    private async Task<(string KeyId, byte[] Key)> GetSigningKeyAsync()
    {
        if (_signingKey is { } cached) return cached;

        await _loadLock.WaitAsync();
        try
        {
            if (_signingKey is { } loaded) return loaded;

            using var connection = connectionFactory.Create();
            var row = await ReadActiveAsync(connection);
            if (row is null)
            {
                // First use: make the secret. The WHERE NOT EXISTS keeps two starting processes from each making one.
                var keyId = Guid.NewGuid().ToString("N");
                await connection.ExecuteAsync("""
                    INSERT INTO web.api_signing_key (KeyId, ProtectedKey)
                    SELECT @keyId, @protectedKey
                    WHERE NOT EXISTS (SELECT 1 FROM web.api_signing_key WHERE IsActive = 1)
                    """, new { keyId, protectedKey = protector.ProtectStored(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))) });
                row = await ReadActiveAsync(connection)
                    ?? throw new InvalidOperationException("The API token signing key could not be created.");
            }

            _signingKey = (row.KeyId, Convert.FromBase64String(protector.UnprotectStored(row.ProtectedKey)));
            return _signingKey.Value;
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private sealed record SigningRow(string KeyId, string ProtectedKey);

    private static Task<SigningRow?> ReadActiveAsync(System.Data.IDbConnection connection) =>
        connection.QuerySingleOrDefaultAsync<SigningRow>(
            "SELECT TOP (1) KeyId, ProtectedKey FROM web.api_signing_key WHERE IsActive = 1 ORDER BY SigningKeyKey DESC");
}
