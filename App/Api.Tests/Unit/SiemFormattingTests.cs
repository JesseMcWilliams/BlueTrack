using System.Text;
using BlueTrack.Api.Data;
using BlueTrack.Api.Siem;
using Xunit;

namespace BlueTrack.Api.Tests.Unit;

/// <summary>D-234: CEF escaping and mapping, the RFC 5424 header, TCP framing, size limits and settings validation.</summary>
public class SiemFormattingTests
{
    [Theory]
    [InlineData(@"a|b", @"a\|b")]
    [InlineData(@"C:\temp", @"C:\\temp")]
    [InlineData(@"x\|y", @"x\\\|y")]
    [InlineData("a=b", "a=b")]                    // equals isn't escaped in the header
    [InlineData("line1\nline2\r", "line1 line2 ")] // no line breaks in the header
    [InlineData("Café 日本", "Café 日本")]
    public void EscapeHeader_EscapesPipeAndBackslash(string input, string expected) =>
        Assert.Equal(expected, CefFormatter.EscapeHeader(input));

    [Theory]
    [InlineData("a=b", @"a\=b")]
    [InlineData(@"C:\temp", @"C:\\temp")]
    [InlineData(@"\=", @"\\\=")]
    [InlineData("a|b", "a|b")]                     // pipe isn't escaped in the extension
    [InlineData("one\ntwo", @"one\ntwo")]
    [InlineData("one\r\ntwo", @"one\r\ntwo")]
    [InlineData("tab\there", "tab here")]
    [InlineData("Ünïcødé ✓", "Ünïcødé ✓")]
    public void EscapeExtension_EscapesEqualsBackslashAndNewlines(string input, string expected) =>
        Assert.Equal(expected, CefFormatter.EscapeExtension(input));

    private static SiemAuditRow Row(Action<SiemAuditRow>? change = null)
    {
        var row = new SiemAuditRow
        {
            AuditEventKey = 4711,
            WriteVersion = 99,
            OccurredAt = new DateTime(2026, 10, 14, 19, 5, 3, 120, DateTimeKind.Utc),
            EventTypeName = "FieldEdit",
            EventTypeDescription = "A governed field was changed",
            UserDisplayName = "Jane | Smith",
            UserExternalIdentifier = "S-1-5-21-1",
            EntityName = "fact_account_progress",
            EntityKey = "42",
            SourceIpAddress = "::ffff:10.1.2.3",
            Detail = "Stage = Onboarded\nby rule",
            Reason = "Regressed: a=b",
            IsForwarded = true,
            CefSeverity = 3,
            FieldChanges = [("CurrentStageKey", "1", "2")]
        };
        change?.Invoke(row);
        return row;
    }

    [Fact]
    public void Format_MapsAnAuditRowToCef()
    {
        var cef = CefFormatter.Format(SiemForwarder.ToCefEvent(Row()), "1.2.3-dev.4+abc1234");

        Assert.StartsWith("CEF:0|BlueTrack|BlueTrack|1.2.3-dev.4+abc1234|FieldEdit|A governed field was changed|3|", cef);
        var extension = cef[(cef.IndexOf("|3|", StringComparison.Ordinal) + 3)..];
        Assert.Contains("rt=1792004703120", extension);   // 2026-10-14T19:05:03.120Z in ms since the epoch
        Assert.Contains("externalId=4711", extension);
        Assert.Contains("suser=Jane | Smith", extension);
        Assert.Contains("suid=S-1-5-21-1", extension);
        Assert.Contains("src=10.1.2.3", extension);       // IPv4-mapped IPv6 sent as IPv4
        Assert.Contains("cs1=fact_account_progress cs1Label=Entity", extension);
        Assert.Contains("cs2=42 cs2Label=Entity Key", extension);
        Assert.Contains("cs3=CurrentStageKey: \"1\" -> \"2\" cs3Label=Field Changes", extension);
        Assert.Contains(@"reason=Regressed: a\=b", extension);
        Assert.EndsWith(@"msg=Stage \= Onboarded\nby rule", extension);
        Assert.DoesNotContain('\n', cef);
    }

    [Fact]
    public void Format_PutsAnIPv6SourceInC6a1_AndLeavesOutEmptyFields()
    {
        var cef = CefFormatter.Format(SiemForwarder.ToCefEvent(Row(r =>
        {
            r.SourceIpAddress = "2001:db8::1";
            r.EntityName = null;
            r.EntityKey = null;
            r.Reason = null;
            r.FieldChanges = [];
        })), "1.0.0");
        Assert.Contains("c6a1=2001:db8::1 c6a1Label=Source IPv6 Address", cef);
        Assert.DoesNotContain("src=", cef);
        Assert.DoesNotContain("cs1=", cef);
        Assert.DoesNotContain("cs3=", cef);
        Assert.DoesNotContain("reason=", cef);
    }

    [Fact]
    public void Format_EscapesHeaderValues_AndClipsTheSeverity()
    {
        var cef = CefFormatter.Format(new CefEvent { SignatureId = @"Odd|Type\", Name = "Name|with\\pipe", Severity = 42 }, "1|0");
        Assert.Equal(@"CEF:0|BlueTrack|BlueTrack|1\|0|Odd\|Type\\|Name\|with\\pipe|10|", cef);
    }

    [Fact]
    public void Format_ClipsMsgToTheDictionaryLength_WithoutSplittingAnEscape()
    {
        var detail = new string('=', 2000);
        var cef = CefFormatter.Format(new CefEvent { SignatureId = "X", Message = detail }, "1");
        var msg = cef[(cef.IndexOf("msg=", StringComparison.Ordinal) + 4)..];
        Assert.Equal(1020 * 2 + 3, msg.Length);   // 1020 escaped '=' plus "..."
        Assert.EndsWith(@"\=...", msg);
    }

    [Fact]
    public void SummarizeFieldChanges_FallsBackToNames_ThenToACount()
    {
        Assert.Null(CefFormatter.SummarizeFieldChanges([]));
        var big = Enumerable.Range(1, 20).Select(i => ($"Field{i}", (string?)new string('a', 100), (string?)new string('b', 100))).ToList();
        Assert.StartsWith("20 fields changed: Field1, Field2", CefFormatter.SummarizeFieldChanges(big));
        var many = Enumerable.Range(1, 200).Select(i => ($"SomeLongFieldName{i}", (string?)"x", (string?)"y")).ToList();
        Assert.Equal("200 fields changed", CefFormatter.SummarizeFieldChanges(many));
        Assert.Equal("F: (empty) -> \"v\"", CefFormatter.SummarizeFieldChanges([("F", null, "v")]));
    }

    [Fact]
    public void SyslogMessage_IsRfc5424()
    {
        var settings = new SiemForwardingSettings { Facility = 20, AppName = "BlueTrack", MaxMessageBytes = 8192 };
        var message = SiemForwarder.BuildMessage(Row(r => r.CefSeverity = 8), settings, "1.0.0", "bt01.corp.example");
        // local4 (20) * 8 + Warning (4, from CEF 8) = 164
        Assert.StartsWith("<164>1 2026-10-14T19:05:03.120Z bt01.corp.example BlueTrack - FieldEdit - CEF:0|", message);
    }

    [Theory]
    [InlineData(0, 6)]
    [InlineData(3, 6)]
    [InlineData(4, 5)]
    [InlineData(6, 5)]
    [InlineData(7, 4)]
    [InlineData(8, 4)]
    [InlineData(9, 2)]
    [InlineData(10, 2)]
    public void SyslogSeverity_FromCefSeverity(int cef, int syslog) => Assert.Equal(syslog, SyslogFormatter.SyslogSeverity(cef));

    [Theory]
    [InlineData(null, 255, "-")]
    [InlineData("", 48, "-")]
    [InlineData("my host", 255, "myhost")]
    [InlineData("hôte", 255, "hte")]
    [InlineData("abcdef", 3, "abc")]
    public void HeaderField_IsPrintableAsciiOrNil(string? input, int max, string expected) =>
        Assert.Equal(expected, SyslogFormatter.HeaderField(input, max));

    [Fact]
    public void OctetCountingFrame_CountsUtf8Octets()
    {
        var frame = SyslogFormatter.Frame("<165>1 - - - - - - CEF:0|é", SyslogFormatter.OctetCounting);
        var text = Encoding.UTF8.GetString(frame);
        var body = Encoding.UTF8.GetBytes("<165>1 - - - - - - CEF:0|é");
        Assert.Equal($"{body.Length} ", text[..(text.IndexOf(' ') + 1)]);
        Assert.Equal(27, body.Length);   // é is two octets
    }

    [Fact]
    public void LineFeedFrame_EndsWithLf_AndHasNoOtherLineBreak()
    {
        var text = Encoding.UTF8.GetString(SyslogFormatter.Frame("a\nb\rc", SyslogFormatter.LineFeed));
        Assert.Equal("a b c\n", text);
    }

    [Fact]
    public void FitToSize_DropsTheFieldChangeSummaryThenClipsMsg()
    {
        var row = Row(r =>
        {
            r.Detail = new string('d', 2000);
            r.Reason = new string('r', 1000);
            r.FieldChanges = Enumerable.Range(1, 5).Select(i => ($"F{i}", (string?)new string('o', 150), (string?)new string('n', 150))).ToList();
        });
        var settings = new SiemForwardingSettings { Facility = 20, AppName = "BlueTrack", MaxMessageBytes = 2048 };
        var message = SiemForwarder.BuildMessage(row, settings, "1.0.0", "host");
        Assert.True(Encoding.UTF8.GetByteCount(message) <= 2048, $"{Encoding.UTF8.GetByteCount(message)} bytes");
        Assert.Contains("cs3=(omitted: too long)", message);

        var roomy = SiemForwarder.BuildMessage(row, With8192(), "1.0.0", "host");
        Assert.DoesNotContain("omitted", roomy);
    }

    private static SiemForwardingSettings With8192() => new() { Facility = 20, AppName = "BlueTrack", MaxMessageBytes = 8192 };

    [Theory]
    [InlineData("sha-1:E1:2D:53:2B:7C:6B:8A:29:A2:76:C8:64:36:0B:08:4B:7A:F1:9E:9D", "SHA1:E12D532B7C6B8A29A276C864360B084B7AF19E9D")]
    [InlineData("e12d532b7c6b8a29a276c864360b084b7af19e9d", "SHA1:E12D532B7C6B8A29A276C864360B084B7AF19E9D")]
    [InlineData("sha-256:" + "AB:" + "00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:00:CD", "SHA256:AB" + "000000000000000000000000000000000000000000000000000000000000" + "CD")]
    [InlineData("sha-1:" + "AB000000000000000000000000000000000000000000000000000000000000CD", null)]   // label and length disagree
    [InlineData("not hex", null)]
    [InlineData("ABCD", null)]
    public void NormalizeFingerprint(string input, string? expected) =>
        Assert.Equal(expected, SiemForwardingValidation.NormalizeFingerprint(input));

    private static SaveSiemForwardingRequest ValidRequest() => new()
    {
        IsEnabled = true, Host = "siem.corp.example", Port = 6514, Transport = "Tls", Framing = "OctetCounting",
        Facility = 20, AppName = "BlueTrack", TimeoutSeconds = 10, MaxMessageBytes = 8192, TlsTrustMode = "System"
    };

    [Fact]
    public void Validate_AcceptsAValidRequest_AndNormalisesCase()
    {
        var request = ValidRequest();
        request.Transport = "tls";
        request.Framing = "linefeed";
        var (errors, settings) = SiemForwardingValidation.Validate(request);
        Assert.Empty(errors);
        Assert.Equal("Tls", settings.Transport);
        Assert.Equal("LineFeed", settings.Framing);
    }

    [Theory]
    [InlineData("Host", "")]
    [InlineData("Host", "evil host;rm")]
    [InlineData("Port", "0")]
    [InlineData("Port", "70000")]
    [InlineData("Transport", "Udp")]
    [InlineData("Framing", "Nul")]
    [InlineData("Facility", "24")]
    [InlineData("TimeoutSeconds", "0")]
    [InlineData("MaxMessageBytes", "1024")]
    [InlineData("AppName", "Blue Track")]
    [InlineData("SyslogHostName", "my host")]
    [InlineData("TlsTrustMode", "IgnoreErrors")]
    [InlineData("Fingerprint", "zz")]
    [InlineData("Ca", "not a certificate")]
    [InlineData("Severity", "11")]
    public void Validate_RejectsBadValues(string field, string value)
    {
        var request = ValidRequest();
        switch (field)
        {
            case "Host": request.Host = value; break;
            case "Port": request.Port = int.Parse(value); break;
            case "Transport": request.Transport = value; break;
            case "Framing": request.Framing = value; break;
            case "Facility": request.Facility = int.Parse(value); break;
            case "TimeoutSeconds": request.TimeoutSeconds = int.Parse(value); break;
            case "MaxMessageBytes": request.MaxMessageBytes = int.Parse(value); break;
            case "AppName": request.AppName = value; break;
            case "SyslogHostName": request.SyslogHostName = value; break;
            case "TlsTrustMode": request.TlsTrustMode = value; break;
            case "Fingerprint": request.TlsTrustMode = "Fingerprint"; request.TlsFingerprint = value; break;
            case "Ca": request.TlsTrustMode = "CaCertificate"; request.TlsCaCertificate = value; break;
            case "Severity": request.EventTypes = [new SaveSiemEventTypeSetting { AuditEventTypeKey = 1, IsForwarded = true, CefSeverity = int.Parse(value) }]; break;
        }
        var (errors, _) = SiemForwardingValidation.Validate(request);
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validate_DropsAPinOrCaTheChosenModeDoesNotUse()
    {
        var request = ValidRequest();
        request.TlsFingerprint = "e12d532b7c6b8a29a276c864360b084b7af19e9d";
        request.TlsCaCertificate = "anything";
        var (errors, settings) = SiemForwardingValidation.Validate(request);
        Assert.Empty(errors);
        Assert.Null(settings.TlsFingerprint);
        Assert.Null(settings.TlsCaCertificate);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(1, 5)]
    [InlineData(2, 10)]
    [InlineData(3, 20)]
    [InlineData(7, 300)]
    [InlineData(50, 300)]
    public void Backoff_DoublesUpToFiveMinutes(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), SiemForwardingBackgroundService.Backoff(failures));
}
