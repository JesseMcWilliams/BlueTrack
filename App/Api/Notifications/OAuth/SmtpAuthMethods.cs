using System.Text.RegularExpressions;
using BlueTrack.Api.Models;

namespace BlueTrack.Api.Notifications.OAuth;

/// <summary>
/// D-223: the SMTP sign-in modes (notification_config.AuthMethod, script 20's
/// CK_notification_config_AuthMethod) and the save-time checks for each.
/// The three OAuth2 modes all end in SASL XOAUTH2 (MailKit's
/// SaslMechanismOAuth2); they differ only in how the access token is got.
/// </summary>
public static partial class SmtpAuthMethods
{
    public const string None = "None";
    public const string Basic = "Basic";
    public const string Microsoft365 = "Microsoft365";
    public const string GoogleWorkspace = "GoogleWorkspace";
    public const string GenericOAuth2 = "GenericOAuth2";

    public static readonly IReadOnlyList<string> All = [None, Basic, Microsoft365, GoogleWorkspace, GenericOAuth2];

    public static bool IsOAuth(string? authMethod) =>
        authMethod is Microsoft365 or GoogleWorkspace or GenericOAuth2;

    /// <summary>
    /// The 400 reasons for a save, in the order a user would fix them; empty
    /// when the request is valid. Basic's own STARTTLS rule (D-198) stays in
    /// the controller, unchanged.
    /// </summary>
    public static List<string> Validate(SaveNotificationConfigRequest request)
    {
        List<string> errors = [];
        if (!All.Contains(request.AuthMethod))
        {
            errors.Add($"AuthMethod must be one of {string.Join(", ", All)}.");
            return errors;
        }
        if (!IsOAuth(request.AuthMethod)) return errors;

        if (!request.EnableStartTls)
            errors.Add("OAuth2 sends an access token to the SMTP server, so STARTTLS must be on.");
        if (request.SmtpCredentialKey is null)
            errors.Add(request.AuthMethod switch
            {
                GoogleWorkspace => "Pick the credential that holds the service account's JSON key.",
                Microsoft365 when request.OAuthClientCredentialType == "Certificate" => "Pick the credential that holds the certificate and private key (PEM).",
                _ => "Pick the credential that holds the client secret."
            });
        if (string.IsNullOrWhiteSpace(request.OAuthUsername) && string.IsNullOrWhiteSpace(request.FromAddress))
            errors.Add("Set the mailbox to sign in as, or a From address.");

        switch (request.AuthMethod)
        {
            case Microsoft365:
                if (string.IsNullOrWhiteSpace(request.OAuthTenantId))
                    errors.Add("Tenant ID is required for Microsoft 365.");
                else if (!TenantIdPattern().IsMatch(request.OAuthTenantId))
                    errors.Add("Tenant ID must be the tenant's GUID or domain name (letters, digits, '.' and '-').");
                if (string.IsNullOrWhiteSpace(request.OAuthClientId))
                    errors.Add("Client ID is required for Microsoft 365.");
                if (request.OAuthClientCredentialType is not ("Secret" or "Certificate"))
                    errors.Add("Client credential type must be Secret or Certificate.");
                break;

            case GenericOAuth2:
                if (string.IsNullOrWhiteSpace(request.OAuthTokenUrl))
                    errors.Add("Token URL is required for Generic OAuth2.");
                else if (!Uri.TryCreate(request.OAuthTokenUrl, UriKind.Absolute, out var tokenUri) || tokenUri.Scheme != Uri.UriSchemeHttps)
                    errors.Add("Token URL must be an absolute https:// address (the client secret is sent to it).");
                if (string.IsNullOrWhiteSpace(request.OAuthClientId))
                    errors.Add("Client ID is required for Generic OAuth2.");
                if (request.OAuthClientAuthStyle is not ("Basic" or "Body"))
                    errors.Add("Client authentication must be Basic (Authorization header) or Body (form fields).");
                break;
        }
        return errors;
    }

    [GeneratedRegex("^[A-Za-z0-9.-]{1,100}$")]
    private static partial Regex TenantIdPattern();
}
