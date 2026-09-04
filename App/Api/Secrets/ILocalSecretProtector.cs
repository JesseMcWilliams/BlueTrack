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
    /// <summary>Encrypts plaintext, returning a value safe to store in a *Reference column (e.g. Base64 ciphertext).</summary>
    string Protect(string plaintext);

    /// <summary>Decrypts a value previously returned by Protect.</summary>
    string Unprotect(string protectedValue);
}
