namespace BlueTrack.Api.Models;

/// <summary>
/// web.identity_provider_config.ConfigurationValues shape for ProviderType
/// "SAML" (D-84). Per D-25, IdP metadata/certificates are pulled once and
/// embedded here as local config -- never fetched dynamically at runtime --
/// so an admin (or a manual refresh action, D-25) fills these in, this app
/// never auto-discovers them. Certificates are referenced by Windows
/// Certificate Store thumbprint (D-34), not stored as blobs here.
/// </summary>
public sealed class SamlProviderSettings
{
    /// <summary>This app's own SAML Entity ID (the SP), e.g. "urn:bluetrack:sp".</summary>
    public string SpEntityId { get; init; } = "";

    /// <summary>
    /// Thumbprint of this app's own signing/decryption certificate, looked
    /// up in the Windows Certificate Store (LocalMachine/My) rather than
    /// stored as a blob here (D-34).
    /// </summary>
    public string SpCertificateThumbprint { get; init; } = "";

    /// <summary>The IdP's Entity ID.</summary>
    public string IdpEntityId { get; init; } = "";

    /// <summary>The IdP's SSO (AuthnRequest) destination URL.</summary>
    public string IdpSingleSignOnDestination { get; init; } = "";

    /// <summary>The IdP's SLO (logout) destination URL, if supported.</summary>
    public string? IdpSingleLogoutDestination { get; init; }

    /// <summary>
    /// Thumbprint of the IdP's signing certificate, pulled once per D-25
    /// (looked up in the Windows Certificate Store, LocalMachine/TrustedPeople
    /// or similar) rather than trusted from dynamically-fetched metadata.
    /// </summary>
    public string IdpCertificateThumbprint { get; init; } = "";

    /// <summary>
    /// Which SAML assertion attribute carries group membership -- varies by
    /// IdP, so configured rather than assumed (GroupIdentifierExtractor's
    /// own comment explains why). A common default is used only as a
    /// starting point, not a guarantee any specific IdP uses it.
    /// </summary>
    public string GroupClaimType { get; init; } = "http://schemas.xmlsoap.org/claims/Group";
}
