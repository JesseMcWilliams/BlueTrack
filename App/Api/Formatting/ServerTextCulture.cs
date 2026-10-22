using System.Globalization;

namespace BlueTrack.Api.Formatting;

/// <summary>
/// D-294: the number and date format of text the server writes for people -- notification emails, and the feed run
/// and Self-Hosted pull summaries the pages show. One installation-wide culture (web.app_config.FormatCulture,
/// Global Application Configuration, default en-US), since an email goes to an address, not a signed-in browser.
/// Audit details, IDs, keys and CSV exports stay invariant: they are records and machine-read.
/// The value is refreshed whenever AppConfigRepository reads the configuration.
/// </summary>
public static class ServerTextCulture
{
    public const string DefaultName = "en-US";

    private static volatile CultureInfo _current = CultureInfo.GetCultureInfo(DefaultName);

    public static CultureInfo Current => _current;

    /// <summary>A specific culture name .NET knows (en-US, de-DE, fr-FR ...), or null.</summary>
    public static CultureInfo? TryGet(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        try
        {
            var culture = CultureInfo.GetCultureInfo(name.Trim());
            return culture.IsNeutralCulture || culture.Equals(CultureInfo.InvariantCulture) ? null : culture;
        }
        catch (CultureNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Uses the named culture; an unknown name leaves the current one.</summary>
    public static void Set(string? name)
    {
        if (TryGet(name) is { } culture) _current = culture;
    }

    /// <summary>"1,234" / "1.234" / "1 234".</summary>
    public static string Count(long value) => value.ToString("N0", Current);

    /// <summary>A value with <paramref name="decimals"/> decimal places, grouped: "1,234.5".</summary>
    public static string Number(double value, int decimals) => value.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), Current);

    /// <summary>A short date: "10/21/2026" / "21.10.2026".</summary>
    public static string Date(DateTime value) => value.ToString("d", Current);

    public static string Date(DateOnly value) => value.ToString("d", Current);

    /// <summary>A short date and time: "10/21/2026 2:05 PM" / "21.10.2026 14:05".</summary>
    public static string DateTime(DateTime value) => value.ToString("g", Current);

    /// <summary>Formats an interpolated string with the current culture: <c>ServerTextCulture.Format($"{n:N0} rows")</c>.</summary>
    public static string Format(FormattableString text) => text.ToString(Current);
}
