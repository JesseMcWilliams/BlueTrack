namespace BlueTrack.Api.Secrets;

/// <summary>
/// D-79: the other half of the ISecretsProvider split. Windows DPAPI isn't
/// a vault with named objects -- it's a local encrypt/decrypt primitive
/// tied to the Windows machine/user context. The "reference" is the
/// ciphertext blob itself, which BlueTrack stores locally (e.g. in
/// identity_provider_config.SecretReference) and hands back here to
/// decrypt -- there's no Safe/Folder/Object to query, so this
/// deliberately does not implement IVaultSecretProvider.
/// </summary>
public interface ILocalSecretProtector
{
    /// <summary>Encrypts plaintext with the default (Machine) scope, returning a value safe to store in a *Reference column (e.g. Base64 ciphertext).</summary>
    string Protect(string plaintext);

    /// <summary>Decrypts a value previously returned by the default-scope Protect.</summary>
    string Unprotect(string protectedValue);

    /// <summary>
    /// D-116: scope-aware overload for web.credential, whose ScopePreference
    /// is admin-selectable (Machine or User) rather than always Machine.
    /// Every existing caller keeps using the parameterless overload above
    /// (Machine scope, unchanged) -- this is additive, not a replacement.
    /// </summary>
    string Protect(string plaintext, CredentialScope scope);

    /// <summary>Decrypts a value previously returned by the scope-aware Protect, using the same scope it was protected with.</summary>
    string Unprotect(string protectedValue, CredentialScope scope);

    /// <summary>
    /// D-215: for the secrets backends' stored credentials (Azure client
    /// secret, AWS secret access key, Conjur API key). User scope -- only the
    /// App Pool's account can decrypt it -- with a random entropy kept in the
    /// value, falling back to Machine scope if the account's profile isn't
    /// loaded. The result says which (see UnprotectStored).
    /// </summary>
    string ProtectStored(string plaintext);

    /// <summary>D-215: decrypts a ProtectStored value, or an older plain Protect value (Machine scope, fixed entropy).</summary>
    string UnprotectStored(string protectedValue);
}

/// <summary>
/// Mirrors System.Security.Cryptography.DataProtectionScope's two values
/// without leaking that BCL type into this interface -- WindowsDpapiProtector
/// maps this onto DataProtectionScope internally.
/// </summary>
public enum CredentialScope
{
    Machine,
    User
}
