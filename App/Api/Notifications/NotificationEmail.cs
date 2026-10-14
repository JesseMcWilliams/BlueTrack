using System.Globalization;
using System.Net;
using System.Text;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// D-222: builds one email as plain, readable HTML plus the same content as
/// plain text. Every value passed in is HTML-encoded here, so callers pass
/// raw data (file names, error messages, justifications) and never encode
/// themselves. BlueTrack has no setting for its own address, so links back
/// are page names ("Risk Exceptions > Overdue Reviews"), not URLs.
/// </summary>
public sealed class NotificationEmail
{
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _html = new();

    public NotificationEmail(string heading)
    {
        _text.AppendLine(heading).AppendLine(new string('=', Math.Min(heading.Length, 70))).AppendLine();
        _html.Append("<h2 style=\"font-size:18px;margin:0 0 12px\">").Append(Encode(heading)).Append("</h2>");
    }

    public NotificationEmail Paragraph(string value)
    {
        _text.AppendLine(value).AppendLine();
        _html.Append("<p style=\"margin:0 0 12px\">").Append(Encode(value)).Append("</p>");
        return this;
    }

    public NotificationEmail Heading(string value)
    {
        _text.AppendLine(value).AppendLine(new string('-', Math.Min(value.Length, 70)));
        _html.Append("<h3 style=\"font-size:15px;margin:16px 0 8px\">").Append(Encode(value)).Append("</h3>");
        return this;
    }

    /// <summary>Label/value pairs, one per line (a two-column table in HTML).</summary>
    public NotificationEmail Facts(IEnumerable<(string Label, string Value)> facts)
    {
        _html.Append("<table style=\"border-collapse:collapse;margin:0 0 12px\">");
        foreach (var (label, value) in facts)
        {
            _text.Append(label).Append(": ").AppendLine(value);
            _html.Append("<tr><th scope=\"row\" style=\"text-align:left;padding:2px 12px 2px 0;vertical-align:top\">")
                 .Append(Encode(label)).Append("</th><td style=\"padding:2px 0\">").Append(Encode(value)).Append("</td></tr>");
        }
        _text.AppendLine();
        _html.Append("</table>");
        return this;
    }

    public NotificationEmail Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        _text.AppendLine(string.Join(" | ", headers));
        _html.Append("<table style=\"border-collapse:collapse;margin:0 0 12px\"><thead><tr>");
        foreach (var header in headers)
        {
            _html.Append("<th scope=\"col\" style=\"text-align:left;border-bottom:1px solid #999;padding:4px 8px\">").Append(Encode(header)).Append("</th>");
        }
        _html.Append("</tr></thead><tbody>");
        foreach (var row in rows)
        {
            _text.AppendLine(string.Join(" | ", row));
            _html.Append("<tr>");
            foreach (var cell in row)
            {
                _html.Append("<td style=\"border-bottom:1px solid #ddd;padding:4px 8px;vertical-align:top\">").Append(Encode(cell)).Append("</td>");
            }
            _html.Append("</tr>");
        }
        _text.AppendLine();
        _html.Append("</tbody></table>");
        return this;
    }

    /// <summary>Where to look in BlueTrack, by page name (no site address setting exists to build a link from).</summary>
    public NotificationEmail SeePage(string pageName) => Paragraph($"In BlueTrack, open {pageName}.");

    public string Text => _text.ToString().TrimEnd() + Environment.NewLine;

    public string Html =>
        "<!DOCTYPE html><html><body style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1f2937\">" +
        _html + "<p style=\"margin:16px 0 0;font-size:12px;color:#6b7280\">Sent by BlueTrack. Notification settings: Admin &gt; Notifications.</p></body></html>";

    public static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");

    /// <summary>Counts as "1,234", whatever the server's culture.</summary>
    public static string Count(int value) => value.ToString("N0", CultureInfo.InvariantCulture);

    /// <summary>"12 of 40 (30.0%)", or "0 of 0 (N/A)".</summary>
    public static string Ratio(int numerator, int denominator) =>
        $"{Count(numerator)} of {Count(denominator)} ({Percent(numerator, denominator)})";

    public static string Percent(int numerator, int denominator) =>
        denominator == 0 ? "N/A" : Math.Round((double)numerator / denominator * 100, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    /// <summary>"yyyy-MM-dd HH:mm" in the server's time zone, from a UTC time.</summary>
    public static string ServerTime(DateTime utc, TimeProvider timeProvider) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeProvider.LocalTimeZone)
            .ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
