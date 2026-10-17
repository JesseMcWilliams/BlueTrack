using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;

namespace BlueTrack.Api.ApiAccess;

/// <summary>
/// D-243: an API key is "bt_" plus 32 random bytes (Base64Url). Only its
/// SHA-256 hash is stored: a key has 256 bits of entropy, so a fast hash is
/// enough, and the key is only ever compared, never recovered. The first
/// characters (KeyPrefix) find the row and are safe to show and log.
/// </summary>
public static class ApiKeyGenerator
{
    public const string KeyStart = "bt_";
    public const int PrefixLength = 11;   // "bt_" and 8 characters

    public static (string Key, string KeyPrefix, byte[] Hash) Create()
    {
        var key = KeyStart + WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return (key, key[..PrefixLength], Hash(key));
    }

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>The prefix of something that looks like a key, or null when it can't be one.</summary>
    public static string? PrefixOf(string? key) =>
        key is not null && key.StartsWith(KeyStart, StringComparison.Ordinal) && key.Length > PrefixLength ? key[..PrefixLength] : null;

    public static bool Matches(string key, byte[] storedHash) => CryptographicOperations.FixedTimeEquals(Hash(key), storedHash);
}
