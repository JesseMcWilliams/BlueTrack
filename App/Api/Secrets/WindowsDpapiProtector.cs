using System.Security.Cryptography;
using System.Text;

namespace BlueTrack.Api.Secrets;

/// <summary>
/// Windows DPAPI (D-36's actual "first backend built" -- built last,
/// once D-79 split it out of the vault-lookup shape it never fit).
/// Uses DataProtectionScope.LocalMachine rather than CurrentUser: this app
/// runs under a dedicated app pool service account (D-30), and
/// LocalMachine scope decrypts correctly regardless of whether that
/// account's Windows user profile is loaded, which CurrentUser scope
/// depends on. Consistent with D-09's single-server assumption -- DPAPI
/// ciphertext is bound to this machine either way (see D-65's own note on
/// the resulting disaster-recovery gap).
/// </summary>
public sealed class WindowsDpapiProtector : ILocalSecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BlueTrack.Secrets.WindowsDpapi");

    public string Protect(string plaintext)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(protectedBytes);
    }

    public string Unprotect(string protectedValue)
    {
        var protectedBytes = Convert.FromBase64String(protectedValue);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(plainBytes);
    }
}
