using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace BlueTrack.Api.Siem;

/// <summary>
/// D-234: one audit row as an ArcSight/OpenText Common Event Format (CEF) message,
/// <c>CEF:0|Device Vendor|Device Product|Device Version|Signature ID|Name|Severity|Extension</c>.
/// Rules from "Implementing ArcSight Common Event Format (CEF)" (Claude_Docs/Design_Audit-Logging.md,
/// "SIEM forwarding", lists the source): the whole message is UTF-8; in the header a pipe and a
/// backslash are escaped with a backslash; in the extension an equals sign and a backslash are escaped,
/// and a newline is written as \n (\r as \r); pipes in the extension aren't escaped. Only keys from the
/// guide's extension dictionary are used, each clipped to the dictionary's length.
/// </summary>
public static class CefFormatter
{
    public const string Vendor = "BlueTrack";
    public const string Product = "BlueTrack";

    // The guide's header lengths: vendor and product 63, version 31, name 512.
    private const int VersionLength = 31;
    private const int SignatureLength = 1023;
    private const int NameLength = 512;

    // The guide's extension lengths.
    public const int MsgLength = 1023;
    public const int ReasonLength = 1023;
    public const int UserLength = 1023;
    public const int CustomStringLength = 4000;
    public const int ExternalIdLength = 40;

    /// <summary>D-234: the field-change summary (cs3) is sent only if it fits in this many characters.</summary>
    public const int FieldChangeSummaryLength = 1000;

    /// <summary>Escapes a header value: backslash and pipe; a line break can't appear in the header, so it becomes a space.</summary>
    public static string EscapeHeader(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var builder = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append(@"\\"); break;
                case '|': builder.Append(@"\|"); break;
                case '\r' or '\n': builder.Append(' '); break;
                default:
                    builder.Append(char.IsControl(c) ? ' ' : c);
                    break;
            }
        }
        return builder.ToString();
    }

    /// <summary>Escapes an extension value: backslash, equals sign, and line breaks as \n and \r.</summary>
    public static string EscapeExtension(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var builder = new StringBuilder(value.Length + 8);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': builder.Append(@"\\"); break;
                case '=': builder.Append(@"\="); break;
                case '\n': builder.Append(@"\n"); break;
                case '\r': builder.Append(@"\r"); break;
                default:
                    // Other control characters (tab, NUL...) would break a line-based reader; send a space.
                    builder.Append(char.IsControl(c) ? ' ' : c);
                    break;
            }
        }
        return builder.ToString();
    }

    /// <summary>Clips to a length in characters without splitting a surrogate pair; "..." marks a clip.</summary>
    public static string Clip(string value, int length)
    {
        if (value.Length <= length) return value;
        var cut = length - 3;
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1])) cut--;
        return value[..Math.Max(cut, 0)] + "...";
    }

    /// <summary>The CEF message for one event. Values are clipped before escaping, so an escape is never cut in half.</summary>
    public static string Format(CefEvent e, string deviceVersion)
    {
        var header = string.Join('|',
            "CEF:0",
            EscapeHeader(Vendor),
            EscapeHeader(Product),
            EscapeHeader(Clip(deviceVersion, VersionLength)),
            EscapeHeader(Clip(e.SignatureId, SignatureLength)),
            EscapeHeader(Clip(string.IsNullOrWhiteSpace(e.Name) ? e.SignatureId : e.Name, NameLength)),
            Math.Clamp(e.Severity, 0, 10).ToString(CultureInfo.InvariantCulture));

        var extension = new List<string>();
        void Add(string key, string? value, int length)
        {
            if (string.IsNullOrEmpty(value)) return;
            extension.Add($"{key}={EscapeExtension(Clip(value, length))}");
        }

        if (e.OccurredAtUtc is { } occurred)
        {
            var utc = DateTime.SpecifyKind(occurred, DateTimeKind.Utc);
            extension.Add($"rt={new DateTimeOffset(utc).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}");
        }
        Add("externalId", e.ExternalId, ExternalIdLength);
        Add("suser", e.UserName, UserLength);
        Add("suid", e.UserId, UserLength);
        var (ipv4, ipv6) = SplitAddress(e.SourceAddress);
        Add("src", ipv4, 45);
        if (ipv6 is not null)
        {
            Add("c6a1", ipv6, 45);
            Add("c6a1Label", "Source IPv6 Address", 1023);
        }
        if (!string.IsNullOrEmpty(e.EntityName))
        {
            Add("cs1", e.EntityName, CustomStringLength);
            Add("cs1Label", "Entity", 1023);
        }
        if (!string.IsNullOrEmpty(e.EntityKey))
        {
            Add("cs2", e.EntityKey, CustomStringLength);
            Add("cs2Label", "Entity Key", 1023);
        }
        if (!string.IsNullOrEmpty(e.FieldChangeSummary))
        {
            Add("cs3", e.FieldChangeSummary, CustomStringLength);
            Add("cs3Label", "Field Changes", 1023);
        }
        Add("reason", e.Reason, ReasonLength);
        Add("msg", e.Message, MsgLength);

        return extension.Count == 0 ? header + "|" : header + "|" + string.Join(' ', extension);
    }

    /// <summary>
    /// src is an IPv4 address in the dictionary; an IPv6 address goes in c6a1 (deviceCustomIPv6Address1) with a label.
    /// An IPv4-mapped IPv6 address (::ffff:a.b.c.d, as Kestrel reports IPv4 clients on a dual-stack socket) is sent as IPv4.
    /// Anything that isn't an IP address is dropped.
    /// </summary>
    public static (string? IPv4, string? IPv6) SplitAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address) || !IPAddress.TryParse(address.Trim(), out var ip)) return (null, null);
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        return ip.AddressFamily == AddressFamily.InterNetwork ? (ip.ToString(), null) : (null, ip.ToString());
    }

    /// <summary>
    /// D-234: a compact "Field: old -> new; ..." summary of an event's field changes, each value clipped to 100
    /// characters. If that is longer than <see cref="FieldChangeSummaryLength"/>, only the field names; if even
    /// that is too long, only the count. Null when there are none.
    /// </summary>
    public static string? SummarizeFieldChanges(IReadOnlyList<(string FieldName, string? OldValue, string? NewValue)> changes)
    {
        if (changes.Count == 0) return null;
        static string Show(string? v) => v is null ? "(empty)" : "\"" + Clip(v, 100) + "\"";
        var full = string.Join("; ", changes.Select(c => $"{c.FieldName}: {Show(c.OldValue)} -> {Show(c.NewValue)}"));
        if (full.Length <= FieldChangeSummaryLength) return full;
        var names = $"{changes.Count} fields changed: {string.Join(", ", changes.Select(c => c.FieldName))}";
        if (names.Length <= FieldChangeSummaryLength) return names;
        return $"{changes.Count} fields changed";
    }
}

/// <summary>D-234: what one CEF message carries (CefFormatter.Format).</summary>
public sealed record CefEvent
{
    public required string SignatureId { get; init; }
    public string? Name { get; init; }
    public int Severity { get; init; }
    public DateTime? OccurredAtUtc { get; init; }
    public string? ExternalId { get; init; }
    public string? UserName { get; init; }
    public string? UserId { get; init; }
    public string? SourceAddress { get; init; }
    public string? EntityName { get; init; }
    public string? EntityKey { get; init; }
    public string? FieldChangeSummary { get; init; }
    public string? Reason { get; init; }
    public string? Message { get; init; }
}
