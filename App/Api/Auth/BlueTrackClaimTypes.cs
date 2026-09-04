namespace BlueTrack.Api.Auth;

/// <summary>
/// App-added claim types, distinct from anything an identity provider
/// issues itself (D-84). ProviderType is stamped onto the ClaimsIdentity
/// during OIDC/SAML sign-in (there's no other reliable way to tell "which
/// identity_provider_config row authenticated this request" once more than
/// one non-Negotiate scheme exists -- see AuthenticatedProviderResolver).
/// Windows Integrated/DevFakeAuth don't need this: both are detected by
/// the presence of a WindowsIdentity instead.
/// </summary>
public static class BlueTrackClaimTypes
{
    public const string ProviderType = "bluetrack:provider_type";
}
