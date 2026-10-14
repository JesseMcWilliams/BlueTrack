using System.Globalization;
using System.Net.NetworkInformation;
using System.Text;

namespace BlueTrack.Api.Siem;

/// <summary>
/// D-234: the RFC 5424 syslog message around a CEF message, and the RFC 6587 / RFC 5425 TCP framing.
///   SYSLOG-MSG = PRI VERSION SP TIMESTAMP SP HOSTNAME SP APP-NAME SP PROCID SP MSGID SP STRUCTURED-DATA SP MSG
/// PRI is &lt;facility * 8 + severity&gt;; VERSION 1; TIMESTAMP the event's time in UTC to the millisecond
/// (TIME-SECFRAC allows 1-6 digits); HOSTNAME, APP-NAME and MSGID are printable US-ASCII (33-126) of at most
/// 255, 48 and 32 characters, "-" (NILVALUE) when empty; PROCID and STRUCTURED-DATA are "-". MSG is the CEF
/// text in UTF-8 without a BOM (RFC 5424's MSG-ANY), so it starts with "CEF:".
/// </summary>
public static class SyslogFormatter
{
    public const string OctetCounting = "OctetCounting";
    public const string LineFeed = "LineFeed";

    /// <summary>CEF severity (0-10) to syslog severity: 0-3 Informational (6), 4-6 Notice (5), 7-8 Warning (4), 9-10 Critical (2).</summary>
    public static int SyslogSeverity(int cefSeverity) => cefSeverity switch
    {
        <= 3 => 6,
        <= 6 => 5,
        <= 8 => 4,
        _ => 2
    };

    public static int Pri(int facility, int severity) => facility * 8 + severity;

    /// <summary>Keeps printable US-ASCII (33-126) only, clipped; "-" when nothing is left.</summary>
    public static string HeaderField(string? value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return "-";
        var builder = new StringBuilder(Math.Min(value.Length, maxLength));
        foreach (var c in value)
        {
            if (builder.Length == maxLength) break;
            if (c is >= '!' and <= '~') builder.Append(c);
        }
        return builder.Length == 0 ? "-" : builder.ToString();
    }

    /// <summary>RFC 5424 TIMESTAMP in UTC with milliseconds, e.g. 2026-10-14T19:05:03.120Z.</summary>
    public static string Timestamp(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>This server's fully qualified name (RFC 5424's first choice for HOSTNAME), else its host name.</summary>
    public static string DefaultHostName()
    {
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();
            return string.IsNullOrWhiteSpace(properties.DomainName)
                ? properties.HostName
                : $"{properties.HostName}.{properties.DomainName}";
        }
        catch (NetworkInformationException)
        {
            return Environment.MachineName;
        }
    }

    public static string Message(int facility, int severity, DateTime occurredAtUtc, string? hostName, string? appName, string? messageId, string msg) =>
        string.Join(' ',
            $"<{Pri(facility, severity).ToString(CultureInfo.InvariantCulture)}>1",
            Timestamp(occurredAtUtc),
            HeaderField(hostName, 255),
            HeaderField(appName, 48),
            "-",
            HeaderField(messageId, 32),
            "-",
            msg);

    /// <summary>
    /// One TCP frame. OctetCounting (RFC 6587 3.4.1, the only framing RFC 5425 defines for TLS):
    /// "MSG-LEN SP SYSLOG-MSG", MSG-LEN the message's length in octets. LineFeed (RFC 6587 3.4.2,
    /// non-transparent): the message then LF; a line break inside it would split it, so any left
    /// (there should be none: CEF escapes them) become spaces.
    /// </summary>
    public static byte[] Frame(string syslogMessage, string framing)
    {
        if (framing == LineFeed)
        {
            var oneLine = syslogMessage.Replace('\r', ' ').Replace('\n', ' ');
            return Encoding.UTF8.GetBytes(oneLine + "\n");
        }

        var body = Encoding.UTF8.GetBytes(syslogMessage);
        var prefix = Encoding.ASCII.GetBytes(body.Length.ToString(CultureInfo.InvariantCulture) + " ");
        var frame = new byte[prefix.Length + body.Length];
        prefix.CopyTo(frame, 0);
        body.CopyTo(frame, prefix.Length);
        return frame;
    }
}
