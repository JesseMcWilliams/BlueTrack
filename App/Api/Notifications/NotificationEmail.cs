using System.Globalization;
using System.Net;
using System.Text;
using BlueTrack.Api.Formatting;

namespace BlueTrack.Api.Notifications;

/// <summary>
/// D-222: builds one email as plain, readable HTML plus the same content as
/// plain text. Every value passed in is HTML-encoded here, so callers pass
/// raw data (file names, error messages, justifications) and never encode
/// themselves. D-224: with a Site URL (web.app_config.SiteUrl) the pages it
/// points to are links; without one they are page names only ("Risk
/// Exceptions > Overdue Reviews").
/// </summary>
public sealed class NotificationEmail
{
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _html = new();
    private readonly string? _siteUrl;

    public NotificationEmail(string heading, string? siteUrl = null)
    {
        _siteUrl = siteUrl;
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

    /// <summary>
    /// Where to look in BlueTrack: "In BlueTrack, open A, B and C[ purpose]."
    /// Each page is a link when the Site URL is set (its address in brackets
    /// in the plain text), else its name only.
    /// </summary>
    public NotificationEmail SeePages(string? purpose, params SitePage[] pages)
    {
        var tail = string.IsNullOrEmpty(purpose) ? "." : $" {purpose}.";
        _text.Append("In BlueTrack, open ").Append(JoinList(pages.Select(p => SiteLinks.Describe(_siteUrl, p)).ToList())).AppendLine(tail).AppendLine();
        _html.Append("<p style=\"margin:0 0 12px\">In BlueTrack, open ")
             .Append(JoinList(pages.Select(PageHtml).ToList()))
             .Append(Encode(tail)).Append("</p>");
        return this;
    }

    public NotificationEmail SeePage(SitePage page, string? purpose = null) => SeePages(purpose, page);

    /// <summary>A page as HTML: a link when the Site URL is set. The address is HTML-encoded for the attribute.</summary>
    private string PageHtml(SitePage page) =>
        SiteLinks.Link(_siteUrl, page) is { } link
            ? $"<a href=\"{Encode(link)}\">{Encode(page.Name)}</a>"
            : Encode(page.Name);

    /// <summary>"A", "A and B", "A, B and C".</summary>
    private static string JoinList(IReadOnlyList<string> items) =>
        items.Count <= 1 ? string.Concat(items) : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];

    public string Text => _text.ToString().TrimEnd() + Environment.NewLine;

    public string Html =>
        "<!DOCTYPE html><html><body style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1f2937\">" +
        _html + "<p style=\"margin:16px 0 0;font-size:12px;color:#6b7280\">Sent by BlueTrack. Notification settings: " +
        PageHtml(SiteLinks.Notifications) + ".</p></body></html>";

    public static string Encode(string? value) => WebUtility.HtmlEncode(value ?? "");

    // D-294: numbers and times in the installation's format culture (Global Application Configuration).

    /// <summary>Counts as "1,234" / "1.234".</summary>
    public static string Count(int value) => ServerTextCulture.Count(value);

    /// <summary>"12 of 40 (30.0%)", or "0 of 0 (N/A)".</summary>
    public static string Ratio(int numerator, int denominator) =>
        $"{Count(numerator)} of {Count(denominator)} ({Percent(numerator, denominator)})";

    public static string Percent(int numerator, int denominator) =>
        denominator == 0 ? "N/A" : ((double)numerator / denominator).ToString("P1", ServerTextCulture.Current);

    /// <summary>A short date and time in the server's time zone, from a UTC time.</summary>
    public static string ServerTime(DateTime utc, TimeProvider timeProvider) =>
        ServerTextCulture.DateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeProvider.LocalTimeZone));
}
