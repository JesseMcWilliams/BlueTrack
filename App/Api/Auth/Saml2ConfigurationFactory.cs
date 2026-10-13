using System.Security.Cryptography.X509Certificates;
using ITfoxtec.Identity.Saml2;
using BlueTrack.Api.Data;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Auth;

/// <summary>
/// Builds a Saml2Configuration (ITfoxtec.Identity.Saml2) from the SAML
/// identity_provider_config row (D-84) -- a placeholder framework, since no
/// real IdP metadata exists for this environment yet. Per D-25, certificate
/// material is never fetched dynamically: both the SP's own signing
/// certificate and the IdP's certificate are looked up by thumbprint in the
/// Windows Certificate Store (D-34), not stored as blobs in the database.
/// Returns null whenever SAML isn't usable yet (row absent/disabled,
/// required fields empty, or a referenced certificate isn't actually
/// installed) -- callers (Saml2Controller) treat that the same way
/// MeController treats a missing WindowsIntegrated row: a clear
/// "not configured" response, not a crash.
/// </summary>
public sealed class Saml2ConfigurationFactory(IdentityProviderRepository identityProviderRepository, SamlTokenReplayCache replayCache)
{
    public async Task<Saml2Configuration?> BuildAsync()
    {
        var provider = await identityProviderRepository.GetByTypeAsync("SAML");
        if (provider is not { IsEnabled: true })
        {
            return null;
        }

        var settings = ProviderSettingsReader.ReadSaml(provider.ConfigurationValues);
        if (settings is null ||
            string.IsNullOrWhiteSpace(settings.SpEntityId) ||
            string.IsNullOrWhiteSpace(settings.IdpEntityId) ||
            string.IsNullOrWhiteSpace(settings.IdpSingleSignOnDestination) ||
            string.IsNullOrWhiteSpace(settings.IdpCertificateThumbprint))
        {
            return null;
        }

        var idpCertificate = FindCertificate(settings.IdpCertificateThumbprint);
        if (idpCertificate is null)
        {
            return null;
        }

        var config = new Saml2Configuration
        {
            Issuer = settings.SpEntityId,
            SingleSignOnDestination = new Uri(settings.IdpSingleSignOnDestination),
            AllowedIssuer = settings.IdpEntityId,
            // D-26: enforced explicitly rather than left at library defaults.
            AudienceRestricted = true,
            // D-198: a response is accepted once only (off by default in ITfoxtec).
            DetectReplayedTokens = true,
            TokenReplayCache = replayCache,
        };
        // D-198: the audience this SP accepts -- the restriction above had an empty list.
        config.AllowedAudienceUris.Add(settings.SpEntityId);
        config.SignatureValidationCertificates.Add(idpCertificate);

        if (!string.IsNullOrWhiteSpace(settings.IdpSingleLogoutDestination))
        {
            config.SingleLogoutDestination = new Uri(settings.IdpSingleLogoutDestination);
        }

        if (!string.IsNullOrWhiteSpace(settings.SpCertificateThumbprint))
        {
            var spCertificate = FindCertificate(settings.SpCertificateThumbprint);
            if (spCertificate is not null)
            {
                config.SigningCertificate = spCertificate;
                config.DecryptionCertificates.Add(spCertificate);
                config.SignAuthnRequest = true;
            }
        }

        return config;
    }

    /// <summary>D-25/D-34: LocalMachine\My, matching where an IIS app pool identity can read a certificate's private key without extra ACL setup.</summary>
    /// <summary>
    /// D-205: the App Pool account's own store (CurrentUser\My) first, so an SP
    /// certificate requested by that account -- whose key only it can read --
    /// works; then LocalMachine\My, as before. CurrentUser needs the App Pool's
    /// "Load User Profile" on; without a profile the store is empty or can't be
    /// opened, and the lookup falls through to LocalMachine.
    /// </summary>
    private static X509Certificate2? FindCertificate(string thumbprint)
    {
        foreach (var location in new[] { StoreLocation.CurrentUser, StoreLocation.LocalMachine })
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
                if (matches.Count > 0) return matches[0];
            }
            catch (System.Security.Cryptography.CryptographicException) when (location == StoreLocation.CurrentUser)
            {
                // No user profile loaded for the App Pool's account: try LocalMachine.
            }
        }
        return null;
    }
}
