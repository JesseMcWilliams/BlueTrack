using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using BlueTrack.Api.Auth;
using BlueTrack.Api.Data;
using BlueTrack.Api.Formatting;

namespace BlueTrack.Api.Notifications;

/// <summary>D-287: finds a SAML certificate by thumbprint where the SAML sign-in looks for it; behind an interface for tests.</summary>
public interface ISamlCertificateLookup
{
    X509Certificate2? Find(string thumbprint);
}

public sealed class StoreSamlCertificateLookup : ISamlCertificateLookup
{
    public X509Certificate2? Find(string thumbprint) => Saml2ConfigurationFactory.FindCertificate(thumbprint);
}

/// <summary>
/// D-287 (D-27): once a day, from the daily digest time, checks the enabled SAML provider's certificates -- the
/// identity provider's signing certificate and BlueTrack's own (SP) certificate, when set -- and emails the type's
/// recipients when one expires within 30 days, within 7 days, has expired, or isn't installed. Each certificate and
/// step is emailed once (DedupKey), so a renewed certificate (a new thumbprint) starts again.
/// </summary>
public sealed class SamlCertificateExpiringCheck(
    NotificationRepository notificationRepository,
    IdentityProviderRepository identityProviderRepository,
    AppConfigRepository appConfigRepository,
    ISamlCertificateLookup certificateLookup,
    TimeProvider timeProvider) : INotificationCheck
{
    public const string TypeName = "SamlCertificateExpiring";
    public const int FirstWarningDays = 30;
    public const int SecondWarningDays = 7;

    public string NotificationTypeName => TypeName;
    public TimeSpan Cooldown => TimeSpan.Zero;   // Unused: every result has a DedupKey.

    public async Task<IReadOnlyList<NotificationCheckResult>> EvaluateAsync()
    {
        var schedule = await notificationRepository.GetScheduleAsync();
        if (DigestSchedule.DailyDueKey(timeProvider.GetLocalNow().DateTime, schedule.DailyDigestTime) is null) return [];

        var provider = await identityProviderRepository.GetByTypeAsync("SAML");
        if (provider is not { IsEnabled: true }) return [];
        var settings = ProviderSettingsReader.ReadSaml(provider.ConfigurationValues);
        if (settings is null) return [];

        var now = timeProvider.GetUtcNow().UtcDateTime;
        var siteUrl = await appConfigRepository.GetSiteUrlAsync();
        var results = new List<NotificationCheckResult>();
        foreach (var (role, thumbprint) in new[] { ("identity provider", settings.IdpCertificateThumbprint), ("BlueTrack SP", settings.SpCertificateThumbprint) })
        {
            if (string.IsNullOrWhiteSpace(thumbprint)) continue;
            var key = $"samlcert-{(role == "identity provider" ? "idp" : "sp")}-{thumbprint.Trim().ToUpperInvariant()}";
            var certificate = certificateLookup.Find(thumbprint.Trim());

            string step, heading, advice;
            if (certificate is null)
            {
                (step, heading, advice) = ("missing", $"The SAML {role} certificate isn't installed",
                    "SAML sign-in fails until it is. Install the certificate with this thumbprint where BlueTrack looks for it (the App Pool account's store or LocalMachine\\My), or set the new thumbprint on Admin > Identity Providers.");
            }
            else
            {
                var daysLeft = (int)Math.Floor((certificate.NotAfter.ToUniversalTime() - now).TotalDays);
                if (daysLeft < 0)
                    (step, heading, advice) = ("expired", $"The SAML {role} certificate has expired", "SAML sign-in fails until the certificate is renewed and its new thumbprint is set on Admin > Identity Providers.");
                else if (daysLeft <= SecondWarningDays)
                    (step, heading, advice) = ("7", $"The SAML {role} certificate expires in {daysLeft} day(s)", RenewAdvice(role));
                else if (daysLeft <= FirstWarningDays)
                    (step, heading, advice) = ("30", $"The SAML {role} certificate expires in {daysLeft} days", RenewAdvice(role));
                else continue;
            }

            var dedupKey = $"{key}-{step}";
            if (await notificationRepository.WasSentAsync(TypeName, dedupKey)) continue;
            var email = new NotificationEmail(heading, siteUrl)
                .Paragraph(advice)
                .Facts([
                    ("Certificate", role),
                    ("Thumbprint", thumbprint.Trim()),
                    ("Subject", certificate?.Subject ?? "not installed"),
                    ("Expires (UTC)", certificate is null ? "unknown" : ServerTextCulture.DateTime(certificate.NotAfter.ToUniversalTime()))
                ])
                .SeePage(SiteLinks.IdentityProviders);
            results.Add(new NotificationCheckResult($"BlueTrack: {heading}", email.Text, $"{role} {thumbprint.Trim()}: {step}", email.Html, dedupKey));
        }
        return results;
    }

    private static string RenewAdvice(string role) => role == "identity provider"
        ? "Ask the identity provider's administrators for the new signing certificate, install it on this server, and set its thumbprint on Admin > Identity Providers before the old one expires, or SAML sign-in stops working."
        : "Renew BlueTrack's SAML certificate, install it with its private key (readable by the App Pool account), give the identity provider the new certificate (the SP metadata), and set its thumbprint on Admin > Identity Providers.";
}
