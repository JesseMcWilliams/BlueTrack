using BlueTrack.Api.ApiAccess;
using BlueTrack.Api.Secrets;
using Xunit;

namespace BlueTrack.Api.Tests.Integration;

/// <summary>
/// D-243: the key format and the token's lifetime and tamper checks. Uses the
/// real signing key in BlueTrackTest (made on first use), and a clock the
/// test moves.
/// </summary>
public class ApiTokenServiceTests
{
    private sealed class MovableTime(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private static ApiTokenService NewService(TimeProvider time) =>
        new(new TestDbConnectionFactory(), new WindowsDpapiProtector(), time);

    [Fact]
    public void Generator_MakesDistinctKeysWithAPrefixAndAMatchingHash()
    {
        var (key, prefix, hash) = ApiKeyGenerator.Create();
        var (other, otherPrefix, _) = ApiKeyGenerator.Create();

        Assert.StartsWith("bt_", key);
        Assert.Equal(46, key.Length);   // "bt_" and 43 Base64Url characters
        Assert.Equal(key[..11], prefix);
        Assert.NotEqual(key, other);
        Assert.NotEqual(prefix, otherPrefix);
        Assert.True(ApiKeyGenerator.Matches(key, hash));
        Assert.False(ApiKeyGenerator.Matches(other, hash));
        Assert.Equal(prefix, ApiKeyGenerator.PrefixOf(key));
        Assert.Null(ApiKeyGenerator.PrefixOf("hello"));
        Assert.Null(ApiKeyGenerator.PrefixOf("bt_short"));
        Assert.Null(ApiKeyGenerator.PrefixOf(null));
    }

    [Fact]
    public async Task Token_IsValidForTenMinutes_ThenExpires()
    {
        var time = new MovableTime(DateTimeOffset.UtcNow);
        var service = NewService(time);

        var (token, expiresIn) = await service.IssueAsync(42, "CT key");
        Assert.Equal(600, expiresIn);
        var identity = await service.ValidateAsync(token);
        Assert.Equal(new ApiTokenIdentity(42, "CT key"), identity);

        time.Advance(TimeSpan.FromMinutes(9));
        Assert.NotNull(await service.ValidateAsync(token));
        time.Advance(TimeSpan.FromMinutes(2));   // 11 minutes: past the 10 plus the 30 seconds' leeway
        Assert.Null(await service.ValidateAsync(token));
    }

    [Fact]
    public async Task Token_FromAnotherSigner_OrAltered_IsRefused()
    {
        var service = NewService(TimeProvider.System);
        var (token, _) = await service.IssueAsync(7, "CT key");

        Assert.Null(await service.ValidateAsync(token[..^2] + (token.EndsWith("AA") ? "BB" : "AA")));
        Assert.Null(await service.ValidateAsync("not.a.token"));
        Assert.Null(await service.ValidateAsync(""));

        // A token signed with some other secret (same claims) must not pass.
        var forged = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler().CreateToken(new Microsoft.IdentityModel.Tokens.SecurityTokenDescriptor
        {
            Issuer = ApiTokenService.Issuer,
            Audience = ApiTokenService.Audience,
            Expires = DateTime.UtcNow.AddMinutes(5),
            Claims = new Dictionary<string, object> { [ApiTokenService.KeyKeyClaim] = 7, ["name"] = "CT key" },
            SigningCredentials = new Microsoft.IdentityModel.Tokens.SigningCredentials(
                new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(new byte[32]), Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256),
        });
        Assert.Null(await service.ValidateAsync(forged));
    }
}
