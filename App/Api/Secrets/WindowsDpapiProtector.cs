using System.Security.Cryptography;
using System.Text;

namespace BlueTrack.Api.Secrets;

/// <summary>
/// Windows DPAPI (D-36's actual "first backend built" -- built last,
/// once D-79 split it out of the vault-lookup shape it never fit).
/// Every existing caller (OIDC ClientSecret, notification_config's old
/// inline password, the vault providers' own ProtectedCredential settings)
/// uses the parameterless Protect/Unprotect below, which stays hardcoded to
/// DataProtectionScope.LocalMachine exactly as before -- this app runs under
/// a dedicated app pool service account (D-30), and LocalMachine scope
/// decrypts correctly regardless of whether that account's Windows user
/// profile is loaded, which CurrentUser scope depends on.
///
/// D-116 adds the scope-aware overloads for web.credential, whose
/// ScopePreference an admin can set to User for tighter isolation (only the
/// app pool identity, not "any process on this machine," can decrypt it).
/// CredentialRepository is the caller that actually chooses User scope --
/// see its own comment for the Machine-then-upgrade-to-User mechanism this
/// enables. Consistent with D-09's single-server assumption either way --
/// DPAPI ciphertext is bound to this one machine regardless of scope (see
/// D-65's own note on the resulting disaster-recovery gap).
/// </summary>
public sealed class WindowsDpapiProtector : ILocalSecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("BlueTrack.Secrets.WindowsDpapi");

    public string Protect(string plaintext) => Protect(plaintext, CredentialScope.Machine);

    public string Unprotect(string protectedValue) => Unprotect(protectedValue, CredentialScope.Machine);

    public string Protect(string plaintext, CredentialScope scope)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var protectedBytes = ProtectedData.Protect(plainBytes, Entropy, ToDataProtectionScope(scope));
        return Convert.ToBase64String(protectedBytes);
    }

    public string Unprotect(string protectedValue, CredentialScope scope)
    {
        var protectedBytes = Convert.FromBase64String(protectedValue);
        var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, ToDataProtectionScope(scope));
        return Encoding.UTF8.GetString(plainBytes);
    }

    private static DataProtectionScope ToDataProtectionScope(CredentialScope scope) =>
        scope == CredentialScope.User ? DataProtectionScope.CurrentUser : DataProtectionScope.LocalMachine;
}
