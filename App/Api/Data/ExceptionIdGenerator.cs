using System.Text.RegularExpressions;

namespace BlueTrack.Api.Data;

/// <summary>
/// Renders web.app_config.ExceptionIdPattern (D-17) against a year and
/// sequence number. See 12_BlueTrack_ExceptionIdNumbering.sql for the
/// supported tokens and the default pattern.
/// </summary>
public static partial class ExceptionIdGenerator
{
    public static string Generate(string pattern, int year, int sequence)
    {
        var result = pattern
            .Replace("{yyyy}", year.ToString("0000"))
            .Replace("{yy}", (year % 100).ToString("00"));

        result = SequenceTokenPattern().Replace(result, match =>
        {
            var digits = match.Groups[1].Value.Length;
            return sequence.ToString(new string('0', digits));
        });

        return result;
    }

    [GeneratedRegex(@"\{seq:(0+)\}")]
    private static partial Regex SequenceTokenPattern();
}
