using BlueTrack.Api.Data;

namespace BlueTrack.Api.Reports;

/// <summary>
/// D-221: the Trends report's inputs, checked once for the report and its
/// export. from/to default to the last 90 days ending today (server date);
/// granularity is day (default) or week. A range may span at most
/// <see cref="MaxDayRangeDays"/> days at Day granularity (two years: about
/// 730 points, as many as a chart can usefully show) and
/// <see cref="MaxWeekRangeDays"/> at Week (five years, about 260 points).
/// </summary>
public sealed record TrendsRequest(DateOnly From, DateOnly To, string Granularity, string? Error)
{
    public const int DefaultRangeDays = 90;
    public const int MaxDayRangeDays = 731;
    public const int MaxWeekRangeDays = 1827;

    public static TrendsRequest Validate(DateOnly? from, DateOnly? to, string? granularity, int? applicationKey, int? safeKey) =>
        Validate(from, to, granularity, applicationKey, safeKey, DateOnly.FromDateTime(DateTime.Now));

    public static TrendsRequest Validate(DateOnly? from, DateOnly? to, string? granularity, int? applicationKey, int? safeKey, DateOnly today)
    {
        var end = to ?? today;
        var start = from ?? end.AddDays(-(DefaultRangeDays - 1));
        var grain = string.IsNullOrWhiteSpace(granularity) ? TrendsRepository.Day : granularity.Trim().ToLowerInvariant();

        string? error = null;
        if (grain is not (TrendsRepository.Day or TrendsRepository.Week))
            error = "granularity must be day or week.";
        else if (start > end)
            error = "from must be on or before to.";
        else if (applicationKey is <= 0)
            error = "applicationKey must be a positive number.";
        else if (safeKey is <= 0)
            error = "safeKey must be a positive number.";
        else
        {
            var days = end.DayNumber - start.DayNumber + 1;
            var max = grain == TrendsRepository.Week ? MaxWeekRangeDays : MaxDayRangeDays;
            if (days > max)
                error = grain == TrendsRepository.Week
                    ? $"The range is {days} days; at Week granularity it can be at most {max} days (five years)."
                    : $"The range is {days} days; at Day granularity it can be at most {max} days (two years). Choose Week for a longer range.";
        }
        return new TrendsRequest(start, end, grain, error);
    }
}
